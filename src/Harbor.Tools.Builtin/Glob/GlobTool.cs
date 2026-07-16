using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

/// <summary>
/// Lists files matching a glob pattern. Honors .gitignore by default.
/// </summary>
public sealed class GlobTool : ITool
{
    public ToolName Name => ToolName.Create("glob");
    public string DisplayName => "Glob";
    public string Description => "Find files matching a glob pattern. Returns matching file paths, one per line. Honors .gitignore by default.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "glob: Find files by pattern";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `glob` to find files by name pattern",
        "Common patterns: `**/*.cs`, `src/**/*.ts`, `*.{json,yaml}`",
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob pattern (e.g. '**/*.cs')" },
            "path": { "type": "string", "description": "Base directory (default: current working directory)" },
            "ignoreGitignore": { "type": "boolean", "description": "Skip .gitignore rules (default: false)" }
          },
          "required": ["pattern"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'pattern'.");
        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var basePath = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : Environment.CurrentDirectory;
        var ignoreGitignore = args.TryGetProperty("ignoreGitignore", out var ig) && ig.GetBoolean();

        if (!Directory.Exists(basePath))
            return Task.FromResult(ToolResult.Error($"Directory not found: {basePath}"));

        var files = EnumerateFiles(basePath, pattern, ignoreGitignore).Take(1000).ToList();

        if (files.Count == 0)
            return Task.FromResult(ToolResult.Success($"No files matching pattern '{pattern}' in {basePath}"));

        var output = string.Join('\n', files.Select(f => Path.GetRelativePath(basePath, f)));
        return Task.FromResult(ToolResult.Success($"Found {files.Count} files:\n{output}", new { count = files.Count, pattern, basePath }));
    }

    private static IEnumerable<string> EnumerateFiles(string basePath, string pattern, bool ignoreGitignore)
    {
        var segments = pattern.Split(['/', '\\'], StringSplitOptions.None);
        var current = new List<string> { basePath };

        foreach (var segment in segments)
        {
            var next = new List<string>();

            foreach (var dir in current)
            {
                if (segment == "**")
                {
                    next.Add(dir);
                    foreach (var d in SafeEnumerateDirs(dir))
                        next.Add(d);
                }
                else if (segment.Contains('*') || segment.Contains('?'))
                {
                    foreach (var d in SafeEnumerateDirs(dir, segment))
                        next.Add(d);

                    foreach (var f in SafeEnumerateFiles(dir, segment))
                        next.Add(f);
                }
                else
                {
                    var combined = Path.Combine(dir, segment);
                    if (Directory.Exists(combined)) next.Add(combined);
                    if (File.Exists(combined)) next.Add(combined);
                }
            }

            current = next;
        }

        var result = current.Where(File.Exists).Distinct();

        if (!ignoreGitignore)
        {
            // Simple .gitignore filter: skip node_modules, bin, obj, .git
            var ignoredDirs = new[] { "node_modules", "bin", "obj", ".git", ".vs", ".idea" };
            result = result.Where(f => !ignoredDirs.Any(d => f.Contains($"/{d}/") || f.Contains($"\\{d}\\")));
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumerateDirs(string path, string pattern = "*")
    {
        try { return Directory.EnumerateDirectories(path, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern = "*")
    {
        try { return Directory.EnumerateFiles(path, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
