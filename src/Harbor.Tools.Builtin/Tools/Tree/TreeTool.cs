using System.Diagnostics;
using System.Text;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Renders an ASCII directory tree. Respects <c>.gitignore</c> when <c>git</c> is
///     available; otherwise falls back to a built-in heavy-dir prune list. Caps depth and
///     entry count to keep output bounded.
/// </summary>
public sealed class TreeTool : ITool
{
    private const int DefaultMaxDepth = 3;
    private const int HardMaxDepth = 10;
    private const int DefaultMaxEntries = 1000;
    private const int HardMaxEntries = 10_000;
    private const int GitTimeoutMs = 4000;

    private static readonly HashSet<string> PrunedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "out",
        "target", "vendor", "__pycache__", ".next", ".nuxt",
        "coverage", ".turbo", ".cache"
    };

    private readonly ILogger<TreeTool> _logger;

    /// <summary>
    ///     Construct a <see cref="TreeTool" />.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public TreeTool(ILogger<TreeTool> logger) { _logger = logger; }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("tree");

    /// <inheritdoc />
    public string DisplayName => "Tree";

    /// <inheritdoc />
    public string Description =>
        "Render an ASCII directory tree. Respects .gitignore when git is available " +
        "(else prunes common build/VCS dirs). Caps depth (default 3) and entries (default 1000).";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "tree: ASCII directory tree (respects .gitignore)";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `tree` to get a quick mental model of a project layout",
        "Default depth=3 — raise for deeper trees, lower for an overview",
        "Set gitignore=false to include node_modules/bin/obj/.git/etc.",
        "Use `ls` for one-level directory listing with sizes and mtimes"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path":           { "type": "string",  "description": "Directory to tree (default: cwd)" },
                                                                          "maxDepth":       { "type": "integer", "description": "Max depth (default: 3, max: 10)" },
                                                                          "includeHidden":  { "type": "boolean", "description": "Include hidden files (default: false)" },
                                                                          "gitignore":      { "type": "boolean", "description": "Respect .gitignore via git ls-files (default: true)" },
                                                                          "maxEntries":     { "type": "integer", "description": "Max entries (default: 1000, max: 10000)" }
                                                                        }
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (args.TryGetProperty("maxDepth", out var d) && d.ValueKind == JsonValueKind.Number
                                                       && d.TryGetInt32(out int depth)
                                                       && (depth < 1 || depth > HardMaxDepth))
            return Result.Failure($"'maxDepth' must be between 1 and {HardMaxDepth}.");

        if (args.TryGetProperty("maxEntries", out var m) && m.ValueKind == JsonValueKind.Number
                                                         && m.TryGetInt32(out int max)
                                                         && (max < 1 || max > HardMaxEntries))
            return Result.Failure($"'maxEntries' must be between 1 and {HardMaxEntries}.");

        return Result.Success();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default) => Task.Run(() => ExecuteCore(args, cancellationToken), cancellationToken);

    private ToolResult ExecuteCore(JsonElement args, CancellationToken ct)
    {
        string path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!
            : Environment.CurrentDirectory;
        int maxDepth = args.TryGetProperty("maxDepth", out var d) && d.ValueKind == JsonValueKind.Number
            ? Math.Clamp(d.GetInt32(), 1, HardMaxDepth)
            : DefaultMaxDepth;
        bool includeHidden = args.TryGetProperty("includeHidden", out var ih) && ih.ValueKind == JsonValueKind.True;
        bool useGitignore = !args.TryGetProperty("gitignore", out var gi) || gi.ValueKind != JsonValueKind.False;
        int maxEntries = args.TryGetProperty("maxEntries", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, HardMaxEntries)
            : DefaultMaxEntries;

        try { path = Path.GetFullPath(path); }
        catch (Exception ex) { return ToolResult.Error($"Invalid path: {ex.Message}"); }

        if (!Directory.Exists(path))
            return ToolResult.Error($"Directory not found: {path}");

        _logger.LogDebug("Tree: {Path} (maxDepth={MaxDepth})", path, maxDepth);

        // Try to get the tracked-files set from `git ls-files` (cached per call).
        var tracked = useGitignore ? TryGetGitTrackedFiles(path) : null;

        var state = new WalkState(maxEntries);
        using var sb = StringBuilderPool.Rent(8192);
        var b = sb.Builder;

        // Root line: show the directory name with trailing slash.
        string rootName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName)) rootName = path;
        b.Append(rootName).Append('/').Append('\n');

        Walk(path, "", 0, maxDepth, includeHidden, tracked, b, state, ct);

        if (state.Truncated)
            b.Append("\n… truncated at ").Append(maxEntries).Append(" entries");

        int dirs = state.Dirs;
        int files = state.Files;

        return ToolResult.Success(
            b.ToString().TrimEnd() + $"\n\n{dirs} director{(dirs == 1 ? "y" : "ies")}, {files} file(s)",
            new
            {
                path,
                maxDepth,
                includeHidden,
                gitignore = useGitignore,
                dirs,
                files,
                truncated = state.Truncated
            });
    }

    private static void Walk(
        string dir,
        string prefix,
        int depth,
        int maxDepth,
        bool includeHidden,
        HashSet<string>? tracked,
        StringBuilder sb,
        WalkState state,
        CancellationToken ct)
    {
        if (depth >= maxDepth || state.Truncated) return;

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetFileSystemInfos();
        }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        // Sort: dirs first, then files; ignore-case ordinal.
        Array.Sort(entries, static (a, b) =>
        {
            bool ad = a is DirectoryInfo;
            bool bd = b is DirectoryInfo;
            if (ad != bd) return ad ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        // Filter hidden + prune list + gitignore.
        var visible = new List<FileSystemInfo>(entries.Length);
        foreach (var e in entries)
        {
            if (!includeHidden && e.Name.StartsWith('.')) continue;

            if (e is DirectoryInfo di)
            {
                if (tracked is null && PrunedDirNames.Contains(e.Name)) continue;
                visible.Add(di);
            }
            else
            {
                if (tracked is not null)
                {
                    string rel = Path.GetRelativePath(dir, e.FullName).Replace('\\', '/');
                    if (!tracked.Contains(rel)) continue;
                }
                visible.Add(e);
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (state.Truncated) return;

            var entry = visible[i];
            bool last = i == visible.Count - 1;
            string branch = last ? "└── " : "├── ";
            string childPrefix = prefix + (last ? "    " : "│   ");

            if (!state.TryAdd()) return;

            sb.Append(prefix).Append(branch).Append(entry.Name);

            if (entry is DirectoryInfo)
            {
                state.DirAdded();
                // If prune list hit (git tracked but dir empty) we still show it.
                sb.Append('/').Append('\n');
                Walk(entry.FullName, childPrefix, depth + 1, maxDepth,
                    includeHidden, tracked, sb, state, ct);
            }
            else
            {
                state.FileAdded();
                sb.Append('\n');
            }
        }
    }

    private static HashSet<string>? TryGetGitTrackedFiles(string root)
    {
        // Returns a set of relative paths (forward slashes) tracked by git.
        // Returns null if git is unavailable or root is not a git repo.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            psi.ArgumentList.Add("ls-files");
            psi.ArgumentList.Add("--cached");
            psi.ArgumentList.Add("--others");
            psi.ArgumentList.Add("--exclude-standard");

            using var p = new Process { StartInfo = psi };
            p.Start();
            // Don't begin async read — read synchronously with a hard timeout.
            if (!p.WaitForExit(GitTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); }
                catch
                { /* ignore */
                }
                return null;
            }
            if (p.ExitCode != 0) return null;

            var set = new HashSet<string>(StringComparer.Ordinal);
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                if (line.Length > 0) set.Add(line.Replace('\\', '/'));
            }
            return set;
        }
        catch
        {
            return null;
        }
    }

    private sealed class WalkState(int maxEntries)
    {
        public int Count { get; private set; }
        public int Dirs { get; private set; }
        public int Files { get; private set; }
        public bool Truncated { get; private set; }

        public bool TryAdd()
        {
            if (Count >= maxEntries)
            {
                Truncated = true;
                return false;
            }
            Count++;
            return true;
        }

        public void DirAdded() => Dirs++;
        public void FileAdded() => Files++;
    }
}
