using System.Text;
namespace Harbor.Tools.Builtin;
/// <summary>
///     Lists directory contents. Returns entries with type (file/dir), size, modified date.
/// </summary>
public sealed class LsTool : ITool
{
    public ToolName Name => ToolName.Create("ls");
    public string DisplayName => "List";
    public string Description => "List directory contents. Returns entries with type (file/dir), size, modified date.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "ls: List directory contents";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `ls` to explore directory structure",
        "Set `all=true` to include hidden files (default: false)"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path": { "type": "string", "description": "Directory to list (default: current)" },
                                                                          "all": { "type": "boolean", "description": "Include hidden files (default: false)" },
                                                                          "recursive": { "type": "boolean", "description": "List recursively (default: false, max depth: 3)" }
                                                                        }
                                                                      }
                                                                      """);

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : Environment.CurrentDirectory;
        bool all = args.TryGetProperty("all", out var a) && a.GetBoolean();
        bool recursive = args.TryGetProperty("recursive", out var r) && r.GetBoolean();

        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Error($"Directory not found: {path}"));

        var sb = new StringBuilder();
        sb.AppendLine($"Contents of {path}:");
        sb.AppendLine();

        int maxDepth = recursive ? 3 : 1;
        ListDirectory(path, "", all, recursive, maxDepth, 0, sb, cancellationToken);

        return Task.FromResult(ToolResult.Success(sb.ToString(), new { path, all, recursive }));
    }

    private static void ListDirectory(
        string path,
        string relativePrefix,
        bool all,
        bool recursive,
        int maxDepth,
        int currentDepth,
        StringBuilder sb,
        CancellationToken ct)
    {
        if (currentDepth >= maxDepth) return;

        IEnumerable<string> dirs;
        IEnumerable<string> files;

        try
        {
            dirs = Directory.EnumerateDirectories(path);
            files = Directory.EnumerateFiles(path);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }

        string indent = new(' ', currentDepth * 2);

        foreach (string dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(dir);
            if (!all && name.StartsWith('.')) continue;

            sb.AppendLine($"{indent}[dir]  {relativePrefix}{name}/");

            if (recursive)
                ListDirectory(dir, $"{relativePrefix}{name}/", all, recursive, maxDepth, currentDepth + 1, sb, ct);
        }

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            if (!all && name.StartsWith('.')) continue;

            var info = new FileInfo(file);
            string size = FormatSize(info.Length);
            string modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            sb.AppendLine($"{indent}[file] {size,10} {modified} {relativePrefix}{name}");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}K",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}M",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}G"
    };
}
