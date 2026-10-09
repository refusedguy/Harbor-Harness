using System.Text;
using Harbor.Abstractions.Lsp;
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

    /// <summary>Language server used for the diagnostics note; null when the host wired no LSP.</summary>
    private readonly ILspService? _lsp;

    /// <summary>Construct an edit tool, optionally with a language server.</summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="lsp">
    ///     Optional language server (#470). Injected by
    ///     <c>ToolsCatalog.CreateToolRegistry</c>; the tool used to reach for the
    ///     per-call service provider that the agent loop always handed it as
    ///     null, so the diagnostics note was dead in production.
    /// </param>
    public EditTool(ILogger<EditTool> logger, ILspService? lsp = null)
    {
        _logger = logger;
        _lsp = lsp;
    }

    public ToolName Name => ToolName.Create("edit");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Path();

    public string DisplayName => "Edit";

    /// <summary>Glyph beside the tool name in every UI surface (#680).</summary>
    public string Glyph => "✎";

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
        Result path = JsonArgValidator.RequiredPath(args);
        if (path.IsFailure)
            return path;

        bool hasSingle = JsonArgValidator.HasString(args, "oldString")
                         && JsonArgValidator.HasString(args, "newString");
        bool hasMulti = JsonArgValidator.HasNonEmptyArray(args, "edits");

        if (!hasSingle && !hasMulti)
            return Result.Failure("Provide edits[] or both oldString and newString.");

        if (hasSingle && string.IsNullOrEmpty(JsonArgs.GetString(args, "oldString")))
            return Result.Failure("oldString must not be empty.");

        if (hasMulti)
        {
            foreach (var e in args.GetProperty("edits").EnumerateArray())
            {
                Result oldString = JsonArgValidator.RequiredNonEmptyString(
                    e, "oldString", "Each edit needs non-empty oldString.");
                if (oldString.IsFailure)
                    return oldString;
                Result newString = JsonArgValidator.RequiredStringPresent(
                    e, "newString", "Each edit needs newString.");
                if (newString.IsFailure)
                    return newString;
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
            // S2 (#376): relative paths resolve against the context working
            // directory (the isolated worktree for sub-agent runs).
            string baseDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
                ? context.WorkingDirectory!
                : Environment.CurrentDirectory;
            path = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(baseDir, rawPath));
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

                    Result<(string Text, int Count)> applied = ApplyEdit(content, oldStr, newStr, replaceAll);
                    if (applied.IsFailure)
                        return ToolResult.Error($"Edit #{step} failed: {applied.Error}");

                    (string newContent, int count) = applied.Value;
                    content = newContent;
                    totalReplacements += count;
                    editSteps++;
                }
            }
            else
            {
                string oldStr = args.GetProperty("oldString").GetString()!;
                string newStr = args.GetProperty("newString").GetString() ?? string.Empty;
                bool replaceAll = GetBool(args, "replaceAll");

                Result<(string Text, int Count)> applied = ApplyEdit(content, oldStr, newStr, replaceAll);
                if (applied.IsFailure)
                    return ToolResult.Error(applied.Error);

                (string newContent, int replacements) = applied.Value;
                content = newContent;
                totalReplacements = replacements;
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

        msg.Append(await DiagnosticsNoteAsync(path, content, cancellationToken).ConfigureAwait(false));

        return ToolResult.Success(
            msg.ToString(),
            new { path, changes = totalReplacements, steps = editSteps });
    }

    /// <summary>
    ///     LSP-aware edit: push the new content to the language server and
    ///     summarize fresh diagnostics. Best-effort — LSP must never break an
    ///     edit. Returns "" when no service, unsupported file, or no diagnostics.
    /// </summary>
    private async Task<string> DiagnosticsNoteAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        try
        {
            // #470: injected by the composition root; an absent language server
            // degrades to no diagnostics note instead of a per-call lookup the
            // agent loop could only ever satisfy with a null provider.
            if (_lsp is not { } lsp)
                return string.Empty;
            if (!lsp.SupportsFile(path))
                return string.Empty;
            await lsp.NotifyChangeAsync(path, content, cancellationToken).ConfigureAwait(false);
            var diagnostics = await lsp.GetDiagnosticsAsync(path, cancellationToken).ConfigureAwait(false);
            if (diagnostics.Count == 0)
                return string.Empty;

            int errors = 0;
            int warnings = 0;
            foreach (var d in diagnostics)
            {
                if (d.Severity == LspSeverity.Error) errors++;
                else if (d.Severity == LspSeverity.Warning) warnings++;
            }

            return $"\n\nLSP: {diagnostics.Count} diagnostic(s) ({errors} error(s), {warnings} warning(s)) — use the lsp tool for details.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LSP diagnostics note failed for {Path}", path);
            return string.Empty;
        }
    }

    /// <summary>
    ///     #721: the outcome rides the Result railway instead of a hand-rolled
    ///     <c>(bool Ok, string? Error)</c> pair. A failed edit used to also carry
    ///     <c>Text = string.Empty</c> and <c>Count = 0</c>, so a caller that read the
    ///     text before checking the flag got a plausible empty file back rather than
    ///     a complaint. Here a failure has no text and no count at all, and
    ///     <c>.Error</c> is read only under an <c>IsFailure</c> guard — the one shape
    ///     CFE0001 models.
    /// </summary>
    private static Result<(string Text, int Count)> ApplyEdit(
        string content, string oldStr, string newStr, bool replaceAll)
    {
        if (string.IsNullOrEmpty(oldStr))
            return Result.Failure<(string Text, int Count)>("oldString must not be empty.");

        if (oldStr == newStr)
            return Result.Failure<(string Text, int Count)>("oldString and newString are identical.");

        if (replaceAll)
        {
            int count = CountOccurrences(content, oldStr);
            if (count == 0)
                return Result.Failure<(string Text, int Count)>($"oldString not found: {Snippet(oldStr)}");

            return Result.Success((content.Replace(oldStr, newStr, StringComparison.Ordinal), count));
        }

        int first = content.IndexOf(oldStr, StringComparison.Ordinal);
        if (first < 0)
            return Result.Failure<(string Text, int Count)>($"oldString not found: {Snippet(oldStr)}");

        int second = content.IndexOf(oldStr, first + oldStr.Length, StringComparison.Ordinal);
        if (second >= 0)
        {
            int total = CountOccurrences(content, oldStr);
            return Result.Failure<(string Text, int Count)>(
                $"oldString found {total} times; make it unique or set replaceAll=true. " +
                $"Snippet: {Snippet(oldStr)}");
        }

        string replaced = string.Concat(
            content.AsSpan(0, first),
            newStr.AsSpan(),
            content.AsSpan(first + oldStr.Length));

        return Result.Success((replaced, 1));
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
}
