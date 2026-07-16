using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

/// <summary>
/// Edits a file by replacing oldString with newString. Supports multi-edit.
/// </summary>
public sealed class EditTool : ITool
{
    public ToolName Name => ToolName.Create("edit");
    public string DisplayName => "Edit";
    public string Description => "Make a string replacement in a file. Either `oldString` → `newString` (single replacement) or `edits` array (multi-edit). The `oldString` must be unique in the file unless `replaceAll` is true.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "edit: String replacement in a file";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `edit` for targeted changes; prefer it over `write` for existing files",
        "Make `oldString` specific enough to be unique in the file",
        "For multiple edits in the same file, use the `edits` array",
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path to edit" },
            "oldString": { "type": "string", "description": "String to find in the file. Must be unique unless replaceAll=true." },
            "newString": { "type": "string", "description": "Replacement string. Use empty string to delete." },
            "replaceAll": { "type": "boolean", "description": "Replace all occurrences of oldString (default: false)" },
            "edits": {
              "type": "array",
              "description": "Multiple edits to apply in sequence",
              "items": {
                "type": "object",
                "properties": {
                  "oldString": { "type": "string" },
                  "newString": { "type": "string" },
                  "replaceAll": { "type": "boolean" }
                },
                "required": ["oldString", "newString"]
              }
            }
          },
          "required": ["path"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'path'.");

        var hasSingle = args.TryGetProperty("oldString", out _) && args.TryGetProperty("newString", out _);
        var hasMulti = args.TryGetProperty("edits", out _);

        if (!hasSingle && !hasMulti)
            return Result.Failure("Either `edits` or both `oldString`+`newString` required.");

        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var path = args.GetProperty("path").GetString()!;

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Environment.CurrentDirectory, path);

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var originalContent = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var newContent = originalContent;
        var changesCount = 0;

        if (args.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in editsEl.EnumerateArray())
            {
                var oldStr = edit.GetProperty("oldString").GetString()!;
                var newStr = edit.GetProperty("newString").GetString()!;
                var replaceAll = edit.TryGetProperty("replaceAll", out var ra) && ra.GetBoolean();

                var (newText, count) = ApplyEdit(newContent, oldStr, newStr, replaceAll);
                if (count == 0)
                    return ToolResult.Error($"oldString not found: {oldStr[..Math.Min(50, oldStr.Length)]}...");

                newContent = newText;
                changesCount += count;
            }
        }
        else
        {
            var oldStr = args.GetProperty("oldString").GetString()!;
            var newStr = args.GetProperty("newString").GetString()!;
            var replaceAll = args.TryGetProperty("replaceAll", out var ra) && ra.GetBoolean();

            var (newText, count) = ApplyEdit(newContent, oldStr, newStr, replaceAll);
            if (count == 0)
                return ToolResult.Error("oldString not found in file.");
            if (count > 1 && !replaceAll)
                return ToolResult.Error($"oldString found {count} times, but replaceAll=false. Make oldString more specific or set replaceAll=true.");

            newContent = newText;
            changesCount = count;
        }

        await File.WriteAllTextAsync(path, newContent, cancellationToken).ConfigureAwait(false);

        var diff = GenerateSimpleDiff(originalContent, newContent);

        return ToolResult.Success($"Edited {path}: {changesCount} replacement(s)\n\nDiff:\n{diff}", new { path, changes = changesCount });
    }

    private static (string result, int count) ApplyEdit(
        string content, string oldStr, string newStr, bool replaceAll)
    {
        if (replaceAll)
        {
            var count = CountOccurrences(content, oldStr);
            return (content.Replace(oldStr, newStr), count);
        }

        var idx = content.IndexOf(oldStr, StringComparison.Ordinal);
        if (idx < 0) return (content, 0);

        var nextIdx = content.IndexOf(oldStr, idx + 1, StringComparison.Ordinal);
        if (nextIdx >= 0) return (content, 2);  // ambiguous

        return (content.Remove(idx, oldStr.Length).Insert(idx, newStr), 1);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string GenerateSimpleDiff(string oldText, string newText)
    {
        var sb = new StringBuilder();
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        var maxLines = Math.Max(oldLines.Length, newLines.Length);
        for (var i = 0; i < maxLines; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;

            if (oldLine == newLine) continue;

            if (oldLine is not null)
                sb.AppendLine($"- {oldLine}");
            if (newLine is not null)
                sb.AppendLine($"+ {newLine}");
        }

        return sb.ToString();
    }
}
