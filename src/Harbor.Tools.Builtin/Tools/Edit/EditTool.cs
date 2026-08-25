using System.Text;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Surgical string replace. oldString must be unique unless replaceAll.
///     Multi-edit applies in order on the updated buffer.
/// </summary>
public sealed class EditTool : ITool
{

    private const int MaxFileChars = 5_000_000;
    private const int MaxDiffLines = 80;
    private const int SnippetLen = 80;
    private readonly ILogger<EditTool> _logger;

    public EditTool(ILogger<EditTool> logger) { _logger = logger; }

    public ToolName Name => ToolName.Create("edit");
    public string DisplayName => "Edit";
    public string Description =>
        "Replace text in a file. Single: oldString→newString. Multi: edits[]. " +
        "oldString must be unique unless replaceAll=true.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "edit: String replacement in a file";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Prefer `edit` over `write` for existing files",
        "Make oldString unique (include surrounding context)",
        "Use edits[] for several replacements in one file",
        "Set replaceAll=true only when every occurrence should change"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path": { "type": "string", "description": "File path to edit" },
                                                                          "oldString": { "type": "string", "description": "Exact text to find (unique unless replaceAll)" },
                                                                          "newString": { "type": "string", "description": "Replacement (empty = delete)" },
                                                                          "replaceAll": { "type": "boolean", "description": "Replace all occurrences (default: false)" },
                                                                          "edits": {
                                                                            "type": "array",
                                                                            "description": "Multiple edits applied in order",
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
        if (!args.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
            return Result.Failure("Missing or empty 'path'.");

        bool hasSingle = args.TryGetProperty("oldString", out var os)
                         && os.ValueKind == JsonValueKind.String
                         && args.TryGetProperty("newString", out var ns)
                         && ns.ValueKind == JsonValueKind.String;
        bool hasMulti = args.TryGetProperty("edits", out var ed)
                        && ed.ValueKind == JsonValueKind.Array
                        && ed.GetArrayLength() > 0;

        if (!hasSingle && !hasMulti)
            return Result.Failure("Provide edits[] or both oldString and newString.");

        if (hasSingle && string.IsNullOrEmpty(os.GetString()))
            return Result.Failure("oldString must not be empty.");

        if (hasMulti)
        {
            foreach (var e in ed.EnumerateArray())
            {
                if (!e.TryGetProperty("oldString", out var o) || o.ValueKind != JsonValueKind.String
                                                              || string.IsNullOrEmpty(o.GetString()))
                    return Result.Failure("Each edit needs non-empty oldString.");
                if (!e.TryGetProperty("newString", out var n) || n.ValueKind != JsonValueKind.String)
                    return Result.Failure("Each edit needs newString.");
            }
        }

        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string rawPath = args.GetProperty("path").GetString()!;

        if (SymlinkGuard.ContainsTraversalSegments(rawPath))
            return ToolResult.Error(
                "Path traversal ('..') is not allowed; provide a direct path without '..' segments.");

        string path;
        try
        {
            path = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, rawPath));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Invalid path: {ex.Message}");
        }

        if (Directory.Exists(path))
            return ToolResult.Error($"Path is a directory: {path}");
        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var symlinkCheck = SymlinkGuard.Check(path);
        if (symlinkCheck.IsFailure)
            return ToolResult.Error(symlinkCheck.Error);

        string original;
        try
        {
            original = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to read: {ex.Message}");
        }

        if (original.Length > MaxFileChars)
            return ToolResult.Error(
                $"File too large to edit in-memory ({original.Length} chars; max {MaxFileChars}).");

        string content = original;
        int totalReplacements = 0;
        int editSteps = 0;

        try
        {
            if (args.TryGetProperty("edits", out var editsEl)
                && editsEl.ValueKind == JsonValueKind.Array
                && editsEl.GetArrayLength() > 0)
            {
                int step = 0;
                foreach (var edit in editsEl.EnumerateArray())
                {
                    step++;
                    string oldStr = edit.GetProperty("oldString").GetString()!;
                    string newStr = edit.GetProperty("newString").GetString() ?? string.Empty;
                    bool replaceAll = GetBool(edit, "replaceAll");

                    var applied = ApplyEdit(content, oldStr, newStr, replaceAll);
                    if (!applied.Ok)
                        return ToolResult.Error($"Edit #{step} failed: {applied.Error}");

                    content = applied.Text;
                    totalReplacements += applied.Count;
                    editSteps++;
                }
            }
            else
            {
                string oldStr = args.GetProperty("oldString").GetString()!;
                string newStr = args.GetProperty("newString").GetString() ?? string.Empty;
                bool replaceAll = GetBool(args, "replaceAll");

                var applied = ApplyEdit(content, oldStr, newStr, replaceAll);
                if (!applied.Ok)
                    return ToolResult.Error(applied.Error!);

                content = applied.Text;
                totalReplacements = applied.Count;
                editSteps = 1;
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Edit failed: {ex.Message}");
        }

        _logger.LogDebug("Editing: {Path} ({Steps} steps, {Replacements} replacements)", path, editSteps, totalReplacements);

        if (totalReplacements == 0 || ReferenceEquals(content, original) || content == original)
        {
            _logger.LogWarning("Edit not found: {Snippet}", Snippet(args.TryGetProperty("oldString", out var os2) ? os2.GetString() ?? "" : ""));
            return ToolResult.Error("No changes applied (oldString not found or identical to newString).");
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(path, content, utf8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ROP-A П.13: boundary message policy lives in one handler.
            return ToolResult.Error(ToolErrors.Handler("edit", cancellationToken, failurePrefix: "Failed to write: ")(ex));
        }

        string diff = GenerateContextDiff(original, content, MaxDiffLines);
        var msg = new StringBuilder();
        msg.Append("Edited ").Append(path)
            .Append(": ").Append(totalReplacements).Append(" replacement(s) in ")
            .Append(editSteps).Append(" edit step(s)");
        if (diff.Length > 0)
            msg.Append("\n\n").Append(diff);

        return ToolResult.Success(
            msg.ToString(),
            new { path, changes = totalReplacements, steps = editSteps });
    }

    private static EditResult ApplyEdit(string content, string oldStr, string newStr, bool replaceAll)
    {
        if (string.IsNullOrEmpty(oldStr))
            return EditResult.Fail("oldString must not be empty.");

        if (oldStr == newStr)
            return EditResult.Fail("oldString and newString are identical.");

        if (replaceAll)
        {
            int count = CountOccurrences(content, oldStr);
            if (count == 0)
                return EditResult.Fail($"oldString not found: {Snippet(oldStr)}");

            return EditResult.Success(content.Replace(oldStr, newStr, StringComparison.Ordinal), count);
        }

        int first = content.IndexOf(oldStr, StringComparison.Ordinal);
        if (first < 0)
            return EditResult.Fail($"oldString not found: {Snippet(oldStr)}");

        int second = content.IndexOf(oldStr, first + oldStr.Length, StringComparison.Ordinal);
        if (second >= 0)
        {
            int total = CountOccurrences(content, oldStr);
            return EditResult.Fail(
                $"oldString found {total} times; make it unique or set replaceAll=true. " +
                $"Snippet: {Snippet(oldStr)}");
        }

        string replaced = string.Concat(
            content.AsSpan(0, first),
            newStr.AsSpan(),
            content.AsSpan(first + oldStr.Length));

        return EditResult.Success(replaced, 1);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length; // non-overlapping
        }
        return count;
    }

    /// <summary>
    ///     Compact unified-ish diff: only changed line ranges with small context.
    ///     Not a full LCS diff — good enough for agent feedback, O(n) lines.
    /// </summary>
    private static string GenerateContextDiff(string oldText, string newText, int maxHunkLines)
    {
        string[] oldLines = SplitLines(oldText);
        string[] newLines = SplitLines(newText);

        // Myers would be ideal; for tool output use simple LCS-free window:
        // find first/last differing region by scanning from start/end.
        int oLen = oldLines.Length;
        int nLen = newLines.Length;

        int prefix = 0;
        while (prefix < oLen && prefix < nLen
                             && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        int oSuffix = oLen - 1;
        int nSuffix = nLen - 1;
        while (oSuffix >= prefix && nSuffix >= prefix
                                 && oldLines[oSuffix] == newLines[nSuffix])
        {
            oSuffix--;
            nSuffix--;
        }

        if (prefix > oSuffix && prefix > nSuffix)
            return "(no line-level diff; same lines / whitespace-only mid-line change)";

        var sb = new StringBuilder();
        sb.AppendLine("Diff (context):");

        const int ctx = 2;
        int fromOld = Math.Max(0, prefix - ctx);
        int toOld = Math.Min(oLen - 1, oSuffix + ctx);
        int fromNew = Math.Max(0, prefix - ctx);
        int toNew = Math.Min(nLen - 1, nSuffix + ctx);

        int linesUsed = 0;

        for (int i = fromOld; i < prefix && linesUsed < maxHunkLines; i++, linesUsed++)
            sb.Append("  ").AppendLine(oldLines[i]);

        for (int i = prefix; i <= oSuffix && i < oLen && linesUsed < maxHunkLines; i++, linesUsed++)
            sb.Append("- ").AppendLine(oldLines[i]);

        for (int i = prefix; i <= nSuffix && i < nLen && linesUsed < maxHunkLines; i++, linesUsed++)
            sb.Append("+ ").AppendLine(newLines[i]);

        for (int i = oSuffix + 1; i <= toOld && linesUsed < maxHunkLines; i++, linesUsed++)
            sb.Append("  ").AppendLine(oldLines[i]);

        if (linesUsed >= maxHunkLines)
            sb.AppendLine("… diff truncated");

        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
    {
        // keep empty trailing line behaviour stable
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string Snippet(string s)
    {
        string t = s.Replace('\n', '⏎').Replace('\r', ' ');
        return t.Length <= SnippetLen ? $"«{t}»" : $"«{t[..SnippetLen]}…»";
    }

    private static bool GetBool(JsonElement args, string name)
        => JsonArgs.GetBool(args, name);

    private readonly record struct EditResult(bool Ok, string Text, int Count, string? Error)
    {
        public static EditResult Success(string text, int count) => new(true, text, count, null);
        public static EditResult Fail(string error) => new(false, string.Empty, 0, error);
    }
}
