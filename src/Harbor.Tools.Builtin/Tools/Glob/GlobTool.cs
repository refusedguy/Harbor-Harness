using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Find files by glob. Matching is delegated to <see cref="Matcher" />
///     (Microsoft.Extensions.FileSystemGlobbing); simple *.{a,b} braces are
///     pre-expanded. Prunes heavy dirs (not a full .gitignore parser).
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
            foreach (string file in ExecuteMatcher(basePath, pat, options.Prune))
            {
                ct.ThrowIfCancellationRequested();
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
    ///     Runs one brace-expanded pattern through <see cref="Matcher" /> and
    ///     returns absolute paths. Only files are returned: the legacy walker
    ///     yielded <c>EnumerateFiles</c> hits, so directory hits are dropped.
    /// </summary>
    private static List<string> ExecuteMatcher(
        string basePath,
        string pattern,
        bool prune)
    {
        pattern = pattern.Replace('\\', '/').Trim('/');
        if (pattern.Length == 0)
            return [];
        // Legacy parity: a trailing "**" never matched — the walker spent it on
        // directory traversal with no file segment left to yield. Matcher would
        // list every file instead, so keep the old answer.
        string[] segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments[^1] == "**")
            return [];

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);
        if (prune)
        {
            // Same set the walker skipped while descending. Excludes win over
            // includes, so even an explicit "bin/*.cs" stays empty — the legacy
            // walk refused pruned names even when named directly.
            foreach (string dir in PrunedDirNames)
                matcher.AddExclude("**/" + dir + "/**");
        }

        var files = new List<string>();
        try
        {
            PatternMatchingResult result = matcher.Execute(
                new DirectoryInfoWrapper(new DirectoryInfo(basePath)));
            foreach (FilePatternMatch match in result.Files)
            {
                string absolute = Path.GetFullPath(Path.Combine(basePath, match.Path));
                if (!Directory.Exists(absolute))
                    files.Add(absolute);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Legacy parity: the walker swallowed per-directory IO faults and
            // yielded what it could, so keep the partial list, not the fault.
        }

        return files;
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
