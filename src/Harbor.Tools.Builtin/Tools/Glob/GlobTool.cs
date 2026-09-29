using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Find files by glob. Supports **, *, ?, and simple *.{a,b} braces.
///     Prunes heavy dirs (not a full .gitignore parser).
/// </summary>
public sealed class GlobTool : ITool
{

    private const int DefaultMaxResults = 1000;
    private const int HardMaxResults = 5000;

    private static readonly HashSet<string> PrunedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "out",
        "target", "vendor", "__pycache__", ".next", ".nuxt",
        "coverage", ".turbo", ".cache"
    };
    private readonly ILogger<GlobTool> _logger;

    public GlobTool(ILogger<GlobTool> logger) { _logger = logger; }

    public ToolName Name => ToolName.Create("glob");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Path();

    public string DisplayName => "Glob";

    /// <summary>Glyph beside the tool name in every UI surface (#680).</summary>
    public string Glyph => "🌐";

    public string Description =>
        "Find files matching a glob (e.g. **/*.cs, src/**/*.ts). " +
        "Returns relative paths. Prunes VCS/build folders by default " +
        "(not a full .gitignore implementation).";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "glob: Find files by pattern";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `glob` to find files by name pattern before read/grep",
        "Patterns: `**/*.cs`, `src/**/*.ts`, `*.json`",
        "Simple braces work: `*.{cs,ts}`",
        "Set ignoreGitignore=true to also search node_modules/bin/obj/etc."
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "pattern": { "type": "string", "description": "Glob pattern (e.g. '**/*.cs')" },
                                                                          "path": { "type": "string", "description": "Base directory (default: cwd)" },
                                                                          "ignoreGitignore": {
                                                                            "type": "boolean",
                                                                            "description": "If true, do not prune node_modules/bin/obj/.git/… (default: false)"
                                                                          },
                                                                          "maxResults": { "type": "integer", "description": "Max paths (default: 1000)" }
                                                                        },
                                                                        "required": ["pattern"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args) =>
        JsonArgValidator.RequiredString(args, "pattern", "Missing or empty 'pattern'.");

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteCore(args, cancellationToken), cancellationToken);

    private sealed record GlobOptions(string Pattern, string BasePath, bool Prune, int MaxResults);

    private ToolResult ExecuteCore(JsonElement args, CancellationToken ct)
    {
        GlobOptions options = ReadOptions(args);

        var resolvedBase = ToolPaths.Resolve(options.BasePath);
        if (resolvedBase.IsFailure)
            return ToolResult.Error(resolvedBase.Error);
        string basePath = resolvedBase.Value;

        if (!Directory.Exists(basePath))
            return ToolResult.Error($"Directory not found: {basePath}");

        _logger.LogDebug("Glob: {Pattern} from {Path}", options.Pattern, basePath);

        List<string> matches;
        bool truncated;
        try
        {
            (matches, truncated) = CollectMatches(basePath, options, ct);
        }
        catch (OperationCanceledException oce)
        {
            // ROP-A П.13: single boundary classifier.
            return ToolResult.Error(ToolErrors.Handler("glob", ct)(oce));
        }

        List<string> relative = ToRelativeSorted(basePath, matches);

        if (relative.Count == 0)
            return ToolResult.Success($"No files matching pattern '{options.Pattern}' in {basePath}");

        _logger.LogDebug("Glob complete: {Count} matches", relative.Count);

        return RenderResult(basePath, options, relative, truncated);
    }

    /// <summary>Reads scalar args into options; base-path resolution stays with the caller.</summary>
    private static GlobOptions ReadOptions(JsonElement args)
    {
        string pattern = args.GetProperty("pattern").GetString()!.Trim();
        string basePath = args.TryGetProperty("path", out var bp) && bp.ValueKind == JsonValueKind.String
            ? bp.GetString()!
            : Environment.CurrentDirectory;
        bool prune = !(args.TryGetProperty("ignoreGitignore", out var ig)
                       && ig.ValueKind == JsonValueKind.True);
        int maxResults = DefaultMaxResults;
        if (args.TryGetProperty("maxResults", out var mr) && mr.ValueKind == JsonValueKind.Number
                                                          && mr.TryGetInt32(out int m))
            maxResults = Math.Clamp(m, 1, HardMaxResults);

        return new GlobOptions(pattern, basePath, prune, maxResults);
    }

    /// <summary>Enumerates all brace-expanded patterns until the match budget is hit.</summary>
    private static (List<string> Matches, bool Truncated) CollectMatches(
        string basePath,
        GlobOptions options,
        CancellationToken ct)
    {
        // Expand light braces: *.{cs,ts} → *.cs + *.ts (one level)
        var patterns = ExpandBraces(options.Pattern);
        var matches = new List<string>(Math.Min(options.MaxResults, 256));
        bool truncated = false;

        foreach (string pat in patterns)
        {
            ct.ThrowIfCancellationRequested();
            foreach (string file in EnumerateGlob(basePath, pat, options.Prune, ct))
            {
                matches.Add(file);
                if (matches.Count >= options.MaxResults)
                {
                    truncated = true;
                    break;
                }
            }
            if (truncated) break;
        }

        return (matches, truncated);
    }

    /// <summary>Dedupes absolute matches into a stable relative sort.</summary>
    private static List<string> ToRelativeSorted(string basePath, List<string> matches)
    {
        // Dedupe + stable sort
        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => Path.GetRelativePath(basePath, f))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Renders the relative match list with a count header.</summary>
    private static ToolResult RenderResult(
        string basePath,
        GlobOptions options,
        List<string> relative,
        bool truncated)
    {
        var sb = new StringBuilder(relative.Count * 40);
        sb.Append("Found ").Append(relative.Count);
        if (truncated) sb.Append('+');
        sb.Append(" files");
        if (truncated) sb.Append(" (truncated at ").Append(options.MaxResults).Append(')');
        sb.Append(':').Append('\n');
        foreach (string r in relative)
            sb.Append(r).Append('\n');

        return ToolResult.Success(
            sb.ToString().TrimEnd(),
            new { count = relative.Count, pattern = options.Pattern, basePath, truncated });
    }

    /// <summary>
    ///     Walk segments. "**" = zero or more directories (recursive), with prune.
    /// </summary>
    private static IEnumerable<string> EnumerateGlob(
        string basePath,
        string pattern,
        bool prune,
        CancellationToken ct)
    {
        pattern = pattern.Replace('\\', '/').Trim('/');
        if (pattern.Length == 0)
            yield break;

        string[] segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // BFS: set of directories that match the prefix so far
        var dirs = new List<string> { basePath };

        for (int si = 0; si < segments.Length; si++)
        {
            ct.ThrowIfCancellationRequested();
            string segment = segments[si];
            bool isLast = si == segments.Length - 1;
            var nextDirs = new List<string>();

            if (segment == "**")
            {
                CollectDoubleStarDirs(dirs, nextDirs, prune, ct);
                // de-dupe dirs
                dirs = nextDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                continue;
            }

            var rx = SegmentToRegex(segment);

            foreach (string dir in dirs)
            {
                ct.ThrowIfCancellationRequested();

                if (!isLast)
                {
                    // must be directories
                    CollectMatchingSubdirs(dir, rx, prune, nextDirs);
                }
                else
                {
                    // last segment: files (and optionally dirs if pattern ends without file — we want files)
                    foreach (string file in SafeEnumerateFiles(dir))
                    {
                        string name = Path.GetFileName(file);
                        if (rx.IsMatch(name))
                            yield return file;
                    }
                }
            }

            if (!isLast)
                dirs = nextDirs;
        }
    }

    /// <summary>Collects every directory under each current dir (including itself).</summary>
    private static void CollectDoubleStarDirs(
        List<string> dirs,
        List<string> nextDirs,
        bool prune,
        CancellationToken ct)
    {
        // All dirs under each current dir (including itself)
        foreach (string dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (string d in EnumerateDirsRecursive(dir, prune, ct))
                nextDirs.Add(d);
        }
    }

    /// <summary>Collects subdirectories of <paramref name="dir" /> matching the segment regex.</summary>
    private static void CollectMatchingSubdirs(
        string dir,
        Regex rx,
        bool prune,
        List<string> nextDirs)
    {
        foreach (string sub in SafeEnumerateDirectories(dir))
        {
            string name = Path.GetFileName(sub);
            if (prune && PrunedDirNames.Contains(name))
                continue;
            if (rx.IsMatch(name))
                nextDirs.Add(sub);
        }
    }

    private static IEnumerable<string> EnumerateDirsRecursive(
        string root,
        bool prune,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            yield return dir;

            foreach (string sub in SafeEnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (prune && PrunedDirNames.Contains(name))
                    continue;
                stack.Push(sub);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try { return Directory.EnumerateFiles(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Glob segment → regex. * ? only; braces already expanded.</summary>
    private static Regex SegmentToRegex(string segment)
    {
        var sb = new StringBuilder(segment.Length * 2);
        sb.Append('^');
        foreach (char ch in segment)
        {
            switch (ch)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>Very small brace expand: one `{a,b}` group per pattern.</summary>
    private static List<string> ExpandBraces(string pattern)
    {
        int start = pattern.IndexOf('{');
        int end = pattern.IndexOf('}');
        if (start < 0 || end <= start)
            return [pattern];

        string before = pattern[..start];
        string after = pattern[(end + 1)..];
        string body = pattern.Substring(start + 1, end - start - 1);
        string[] parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return [pattern];

        var list = new List<string>(parts.Length);
        foreach (string part in parts)
            list.Add(before + part + after);
        return list;
    }
}
