using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
/// Lists directory contents (type, size, mtime). Caps output; prunes heavy dirs when recursive.
/// </summary>
public sealed class LsTool : ITool
{
    private readonly ILogger<LsTool> _logger;

    public LsTool(ILogger<LsTool> logger) { _logger = logger; }

    private static readonly HashSet<string> PrunedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "out",
        "target", "vendor", "__pycache__", ".next", ".nuxt",
        "coverage", ".turbo", ".cache"
    };

    private const int DefaultMaxEntries = 500;
    private const int HardMaxEntries = 2000;
    private const int DefaultMaxDepth = 3;

    public ToolName Name => ToolName.Create("ls");
    public string DisplayName => "List";
    public string Description =>
        "List directory contents. Entries: [dir]/[file] with size and modified time for files. " +
        "Recursive mode prunes VCS/build folders and caps total entries.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "ls: List directory contents";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `ls` to explore directory structure before reading files",
        "Prefer non-recursive first; use recursive only when needed",
        "Set `all=true` to include hidden files (default: false)",
        "Use `path` for a specific directory; default is the working directory"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path":      { "type": "string",  "description": "Directory to list (default: cwd)" },
                                                                          "all":       { "type": "boolean", "description": "Include hidden files (default: false)" },
                                                                          "recursive": { "type": "boolean", "description": "List recursively (default: false)" },
                                                                          "depth":     { "type": "integer", "description": "Max recursion depth when recursive (default: 3, max: 8)" },
                                                                          "maxEntries":{ "type": "integer", "description": "Max entries to return (default: 500)" }
                                                                        }
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (args.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number
                                                    && d.TryGetInt32(out var depth) && depth < 1)
            return Result.Failure("depth must be >= 1");

        if (args.TryGetProperty("maxEntries", out var m) && m.ValueKind == JsonValueKind.Number
                                                         && m.TryGetInt32(out var max) && max < 1)
            return Result.Failure("maxEntries must be >= 1");

        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // Listing can be large when recursive — don't burn the agent loop thread.
        return Task.Run(() => ExecuteCore(args, cancellationToken), cancellationToken);
    }

    private ToolResult ExecuteCore(JsonElement args, CancellationToken ct)
    {
        var path = GetString(args, "path") ?? Environment.CurrentDirectory;
        var all = GetBool(args, "all");
        var recursive = GetBool(args, "recursive");
        var depthArg = GetInt(args, "depth");
        var maxEntriesArg = GetInt(args, "maxEntries");

        var maxDepth = recursive
            ? Math.Clamp(depthArg ?? DefaultMaxDepth, 1, 8)
            : 1;
        var maxEntries = Math.Clamp(maxEntriesArg ?? DefaultMaxEntries, 1, HardMaxEntries);

        path = Path.GetFullPath(path);

        if (!Directory.Exists(path))
            return ToolResult.Error($"Directory not found: {path}");

        _logger.LogDebug("Ls: {Path} (recursive={Recursive})", path, recursive);

        var sb = new StringBuilder(Math.Min(maxEntries, 256) * 48);
        sb.Append("Contents of ").Append(path).Append(':').Append('\n');

        var state = new WalkState(maxEntries);
        try
        {
            ListDirectory(
                path,
                relativePrefix: "",
                all,
                recursive,
                maxDepth,
                currentDepth: 0,
                sb,
                state,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ToolResult.Error("ls cancelled");
        }

        if (state.Count == 0)
            sb.AppendLine("(empty)");

        if (state.Truncated)
        {
            sb.Append('\n')
                .Append("… truncated at ")
                .Append(maxEntries)
                .Append(" entries (pass maxEntries/depth or narrow path)");
        }

        return ToolResult.Success(
            sb.ToString().TrimEnd(),
            new
            {
                path,
                all,
                recursive,
                depth = maxDepth,
                count = state.Count,
                truncated = state.Truncated
            });
    }

    private static void ListDirectory(
        string path,
        string relativePrefix,
        bool all,
        bool recursive,
        int maxDepth,
        int currentDepth,
        StringBuilder sb,
        WalkState state,
        CancellationToken ct)
    {
        if (currentDepth >= maxDepth || state.Truncated)
            return;

        FileSystemInfo[] entries;
        try
        {
            // One pass: dirs + files with metadata, no extra FileInfo per path.
            entries = new DirectoryInfo(path).GetFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        // Stable, agent-friendly order: dirs first, then files; ordinal ignore-case.
        Array.Sort(entries, static (a, b) =>
        {
            var aDir = a is DirectoryInfo;
            var bDir = b is DirectoryInfo;
            if (aDir != bDir)
                return aDir ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var indent = currentDepth == 0 ? "" : new string(' ', currentDepth * 2);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (state.Truncated)
                return;

            var name = entry.Name;
            if (!all && name.StartsWith('.'))
                continue;

            if (entry is DirectoryInfo dir)
            {
                // Always show the dir row; prune children of heavy folders when recursive.
                if (!state.TryAdd())
                {
                    AppendTruncationNote(sb);
                    return;
                }

                sb.Append(indent)
                    .Append("[dir]  ")
                    .Append(relativePrefix)
                    .Append(name)
                    .Append('/')
                    .Append('\n');

                if (!recursive)
                    continue;

                if (PrunedDirNames.Contains(name))
                {
                    // Mark prune so the model knows we skipped the dump.
                    if (state.TryAdd())
                    {
                        sb.Append(indent)
                            .Append("       ")
                            .Append("(pruned)")
                            .Append('\n');
                    }
                    continue;
                }

                ListDirectory(
                    dir.FullName,
                    $"{relativePrefix}{name}/",
                    all,
                    recursive,
                    maxDepth,
                    currentDepth + 1,
                    sb,
                    state,
                    ct);
            }
            else if (entry is FileInfo file)
            {
                if (!state.TryAdd())
                {
                    AppendTruncationNote(sb);
                    return;
                }

                long length;
                DateTime mtime;
                try
                {
                    length = file.Length;
                    mtime = file.LastWriteTime;
                }
                catch
                {
                    length = 0;
                    mtime = DateTime.MinValue;
                }

                sb.Append(indent)
                    .Append("[file] ")
                    .Append(FormatSize(length).PadLeft(10))
                    .Append(' ')
                    .Append(mtime.ToString("yyyy-MM-dd HH:mm"))
                    .Append(' ')
                    .Append(relativePrefix)
                    .Append(name)
                    .Append('\n');
            }
        }
    }

    private static void AppendTruncationNote(StringBuilder sb)
        => sb.AppendLine("…");

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool GetBool(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                                                 && el.GetBoolean();

    private static int? GetInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
                                                 && el.TryGetInt32(out var n)
            ? n
            : null;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}K",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}M",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}G"
    };

    private sealed class WalkState(int maxEntries)
    {
        public int Count { get; private set; }
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
    }
}
