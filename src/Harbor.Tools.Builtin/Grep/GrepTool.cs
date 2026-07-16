using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

/// <summary>
/// Searches file contents with regex. Returns matching lines with file:line:content format.
/// </summary>
public sealed class GrepTool : ITool
{
    public ToolName Name => ToolName.Create("grep");
    public string DisplayName => "Grep";
    public string Description => "Search file contents with regex. Returns matching lines with file:line:content format. Searches recursively from the given path.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "grep: Search file contents";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `grep` to find code or text by content",
        "Pattern is a regular expression",
        "Use `include` to limit search to specific file types (e.g. '*.cs')",
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regular expression pattern" },
            "path": { "type": "string", "description": "Base directory or file to search (default: current)" },
            "include": { "type": "string", "description": "File name glob to include (e.g. '*.cs')" },
            "ignoreCase": { "type": "boolean", "description": "Case-insensitive search (default: false)" },
            "maxResults": { "type": "integer", "description": "Maximum number of matches to return (default: 100)" }
          },
          "required": ["pattern"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'pattern'.");
        try
        {
            _ = new Regex(p.GetString()!);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Invalid regex: {ex.Message}");
        }
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : Environment.CurrentDirectory;
        var include = args.TryGetProperty("include", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        var ignoreCase = args.TryGetProperty("ignoreCase", out var ic) && ic.GetBoolean();
        var maxResults = args.TryGetProperty("maxResults", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 100;

        var options = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var regex = new Regex(pattern, options);

        var results = new List<string>();
        var totalMatches = 0;

        if (File.Exists(path))
        {
            var matches = await GrepFileAsync(path, regex, cancellationToken).ConfigureAwait(false);
            foreach (var match in matches)
            {
                if (totalMatches >= maxResults) break;
                results.Add(match);
                totalMatches++;
            }
        }
        else if (Directory.Exists(path))
        {
            var files = EnumerateFiles(path, include);
            foreach (var file in files)
            {
                if (totalMatches >= maxResults) break;
                cancellationToken.ThrowIfCancellationRequested();

                var matches = await GrepFileAsync(file, regex, cancellationToken).ConfigureAwait(false);
                foreach (var match in matches)
                {
                    if (totalMatches >= maxResults) break;
                    results.Add(match);
                    totalMatches++;
                }
            }
        }
        else
        {
            return ToolResult.Error($"Path not found: {path}");
        }

        if (results.Count == 0)
            return ToolResult.Success($"No matches for pattern '{pattern}' in {path}");

        return ToolResult.Success(
            $"Found {results.Count} matches:\n{string.Join('\n', results)}", new { count = results.Count, pattern, path });
    }

    private static async Task<List<string>> GrepFileAsync(string file, Regex regex, CancellationToken ct)
    {
        var results = new List<string>();
        try
        {
            using var reader = new StreamReader(file);
            var lineNum = 0;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lineNum++;
                if (regex.IsMatch(line))
                    results.Add($"{file}:{lineNum}: {line}");
            }
        }
        catch
        {
            // Skip unreadable files
        }
        return results;
    }

    private static IEnumerable<string> EnumerateFiles(string path, string? include)
    {
        var ignoredDirs = new[] { "node_modules", "bin", "obj", ".git", ".vs", ".idea" };

        var allFiles = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => !ignoredDirs.Any(d => f.Contains($"/{d}/") || f.Contains($"\\{d}\\")));

        if (!string.IsNullOrEmpty(include))
        {
            var includeRegex = GlobToRegex(include);
            allFiles = allFiles.Where(f => includeRegex.IsMatch(Path.GetFileName(f)));
        }

        return allFiles;
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase);
    }
}
