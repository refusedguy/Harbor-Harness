using Microsoft.Extensions.Logging;
using Harbor.Abstractions.Models.Identifiers;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;

namespace Harbor.Plugin.FileTree;

/// <summary>
/// FileTree plugin — adds a `tree` tool that visualizes directory structure.
/// Demonstrates a read-only tool with custom output formatting.
/// </summary>
public sealed class FileTreePlugin : IToolPlugin
{
    public string Name => "filetree";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Directory tree visualization tool";

    public void Initialize(PluginContext context)
    {
        context.CreateLogger<FileTreePlugin>().LogInformation("FileTree plugin initialized");
    }

    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<TreeTool>();
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class TreeTool : ITool
{
    private static readonly string[] IgnoredDirs =
    [
        "node_modules", "bin", "obj", ".git", ".vs", ".idea", ".vscode",
        "__pycache__", ".pytest_cache", "dist", "build", "target",
        ".next", ".nuxt", ".gradle", ".mvn"
    ];

    public ToolName Name => ToolName.Create("tree");
    public string DisplayName => "Tree";
    public string Description => "Display directory structure as a tree. Useful for understanding project layout. Honors common ignore patterns (node_modules, bin, obj, .git, etc.).";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "tree: Visualize directory structure";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `tree` to get an overview of project structure",
        "Set `depth` to limit recursion (default: 3, max: 10)",
        "Set `all=true` to include hidden files",
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory to visualize (default: current)" },
            "depth": { "type": "integer", "description": "Maximum recursion depth (default: 3, max: 10)" },
            "all": { "type": "boolean", "description": "Include hidden files (default: false)" }
          }
        }
        """);

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!
            : Environment.CurrentDirectory;
        var depth = args.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number
            ? Math.Min(Math.Max(d.GetInt32(), 1), 10)
            : 3;
        var all = args.TryGetProperty("all", out var a) && a.GetBoolean();

        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Error($"Directory not found: {path}"));

        var sb = new StringBuilder();
        sb.AppendLine(path);
        RenderTree(path, "", depth, 0, all, sb, cancellationToken);
        return Task.FromResult(ToolResult.Success(sb.ToString(), new { path, depth, all }));
    }

    private static void RenderTree(
        string path, string prefix, int maxDepth, int currentDepth,
        bool all, StringBuilder sb, CancellationToken ct)
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

        var dirList = dirs
            .Where(d => all || !Path.GetFileName(d).StartsWith('.'))
            .Where(d => !IgnoredDirs.Contains(Path.GetFileName(d)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileList = files
            .Where(f => all || !Path.GetFileName(f).StartsWith('.'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allEntries = dirList.Count + fileList.Count;
        for (var i = 0; i < allEntries; i++)
        {
            ct.ThrowIfCancellationRequested();

            var isLast = i == allEntries - 1;
            var connector = isLast ? "└── " : "├── ";
            var childPrefix = prefix + (isLast ? "    " : "│   ");

            if (i < dirList.Count)
            {
                var dir = dirList[i];
                sb.AppendLine($"{prefix}{connector}{Path.GetFileName(dir)}/");
                RenderTree(dir, childPrefix, maxDepth, currentDepth + 1, all, sb, ct);
            }
            else
            {
                var file = fileList[i - dirList.Count];
                var info = new FileInfo(file);
                var size = FormatSize(info.Length);
                sb.AppendLine($"{prefix}{connector}{Path.GetFileName(file)} ({size})");
            }
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}K",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}M",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}G",
    };
}
