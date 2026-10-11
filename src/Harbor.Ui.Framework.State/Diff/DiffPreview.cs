using System.Text;
using System.Text.Json;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Builds the diff payload for a file-touching tool call: the file path, a
///     short inline preview and the full diff behind the expand path.
/// </summary>
/// <remarks>
///     <para>
///         This lives in the state layer, not in a renderer, because
///         <see cref="ChatAppReducer" /> runs it ONCE when the call starts and
///         publishes the result on <see cref="ToolCallSnapshot" />. Before #680
///         every rendering path ran its own copy per card, and the desktop
///         chat view-model's copy was dead: <c>ParseToolLine</c> never set the
///         <c>IsDiffTool</c> flag, so the <c>if (parsed.IsDiffTool)</c> branch
///         that should have carried the diff into the UI never ran.
///     </para>
///     <para>
///         THE ONE PRODUCER OF THE CONTEXT-DIFF BLOCK (#570). This used to say the
///         CellForge renderer "keeps its own allocation-tuned port … because it is
///         called from the paint path, where it is benchmarked". All three clauses
///         were false, and the sentence is why the copy survived: CellForge never
///         called <c>ExtractDiff</c> — the card's diff text arrives already built,
///         and <c>ToolCallBlock.DiffRenderer</c> reached the copy only for two
///         constants, which C# inlines at compile time, so nothing noticed there
///         was no caller. There was no benchmark either: the two
///         <c>ExtractDiff_*</c> tripwires that cited it measured a method nothing
///         invoked, and both rows are struck from BENCHMARKS.md rather than
///         re-pointed here, because this runs ONCE per tool call from
///         <see cref="ChatAppReducer" />, not per frame.
///     </para>
///     <para>
///         The copy is gone, and the pair it made is now inside the perimeter of
///         <c>DiffSurfaceNameCollisionRule</c> — which could not see it before,
///         because both files re-implement the alignment instead of calling
///         <c>LineDiff</c>, and that rule's perimeter was "calls the engine".
///     </para>
/// </remarks>
public static class DiffPreview
{
    /// <summary>Visible preview budget — six lines of context around the change.</summary>
    public const int MaxPreviewLines = 6;

    /// <summary>Full-diff budget backing the expand path.</summary>
    public const int MaxFullDiffLines = 80;

    /// <summary>Sentinel when old/new share all lines (whitespace-only mid-line change).</summary>
    public const string NoLineDiffSentinel = "(no line-level diff; same lines / whitespace-only mid-line change)";

    /// <summary>Overflow marker for context diffs and patch passthrough.</summary>
    public const string DiffTruncatedSentinel = "… diff truncated";

    /// <summary>Overflow marker for whole-content (<c>write</c>) diffs.</summary>
    public const string ContentTruncatedSentinel = "… truncated";

    /// <summary>Placeholder when no file/path-like string field is found in the arguments.</summary>
    public const string UnknownPath = "<unknown>";

    /// <summary>
    ///     Extracts a diff payload for a file-touching tool call. Anything else —
    ///     a different tool, missing arguments, malformed JSON — degrades to
    ///     "not a diff call" with null payloads rather than throwing.
    /// </summary>
    /// <param name="toolName">The tool's stable name.</param>
    /// <param name="argsJson">The raw arguments the call was made with.</param>
    public static (bool IsDiffTool, string? FilePath, string? Preview, string? FullDiff) ExtractDiff(
        string toolName, string argsJson)
    {
        if (toolName != "edit" && toolName != "write" && toolName != "patch")
        {
            return (false, null, null, null);
        }

        // One parse feeds every branch: the file path plus whichever payload
        // field this tool's arguments carry.
        string filePath = UnknownPath;
        string? oldString = null;
        string? newString = null;
        string? content = null;
        string? patch = null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (false, null, null, null);
            }

            JsonElement root = doc.RootElement;
            filePath = FindFilePath(root);
            if (root.TryGetProperty("oldString", out var osEl) && osEl.ValueKind == JsonValueKind.String)
            {
                oldString = osEl.GetString();
            }

            if (root.TryGetProperty("newString", out var nsEl) && nsEl.ValueKind == JsonValueKind.String)
            {
                newString = nsEl.GetString();
            }

            if (root.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
            {
                content = cEl.GetString();
            }

            if (root.TryGetProperty("patch", out var pEl) && pEl.ValueKind == JsonValueKind.String)
            {
                patch = pEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed arguments — not a diff call. The card still renders.
            return (false, null, null, null);
        }

        if (toolName == "edit")
        {
            if (!string.IsNullOrEmpty(oldString) && newString != null)
            {
                return (
                    true,
                    filePath,
                    GenerateContextDiff(oldString, newString, MaxPreviewLines),
                    GenerateContextDiff(oldString, newString, MaxFullDiffLines));
            }
        }
        else if (toolName == "write")
        {
            if (!string.IsNullOrEmpty(content))
            {
                return (
                    true,
                    filePath,
                    GenerateContentDiff(content, MaxPreviewLines),
                    GenerateContentDiff(content, MaxFullDiffLines));
            }
        }
        else // patch
        {
            if (!string.IsNullOrEmpty(patch))
            {
                return (true, filePath, TruncateLines(patch, MaxPreviewLines), patch);
            }
        }

        return (false, null, null, null);
    }

    /// <summary>
    ///     Scans an already-parsed arguments object for the first string-valued
    ///     field whose name contains <c>file</c> or <c>path</c> (case-insensitive),
    ///     else <see cref="UnknownPath" />.
    /// </summary>
    private static string FindFilePath(JsonElement root)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String
                && (prop.Name.Contains("file", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Contains("path", StringComparison.OrdinalIgnoreCase)))
            {
                return prop.Value.GetString() ?? UnknownPath;
            }
        }

        return UnknownPath;
    }

    private static string GenerateContextDiff(string oldText, string newText, int maxHunkLines)
    {
        string[] oldLines = SplitLines(oldText);
        string[] newLines = SplitLines(newText);

        int oLen = oldLines.Length;
        int nLen = newLines.Length;

        int prefix = 0;
        while (prefix < oLen && prefix < nLen && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        int oSuffix = oLen - 1;
        int nSuffix = nLen - 1;
        while (oSuffix >= prefix && nSuffix >= prefix && oldLines[oSuffix] == newLines[nSuffix])
        {
            oSuffix--;
            nSuffix--;
        }

        if (prefix > oSuffix && prefix > nSuffix)
        {
            return NoLineDiffSentinel;
        }

        var sb = new StringBuilder();

        const int ctx = 2;
        int fromOld = Math.Max(0, prefix - ctx);
        int toOld = Math.Min(oLen - 1, oSuffix + ctx);

        int linesUsed = 0;

        for (int i = fromOld; i < prefix && linesUsed < maxHunkLines; i++, linesUsed++)
        {
            sb.Append("  ").AppendLine(oldLines[i]);
        }

        for (int i = prefix; i <= oSuffix && i < oLen && linesUsed < maxHunkLines; i++, linesUsed++)
        {
            sb.Append("- ").AppendLine(oldLines[i]);
        }

        for (int i = prefix; i <= nSuffix && i < nLen && linesUsed < maxHunkLines; i++, linesUsed++)
        {
            sb.Append("+ ").AppendLine(newLines[i]);
        }

        for (int i = oSuffix + 1; i <= toOld && linesUsed < maxHunkLines; i++, linesUsed++)
        {
            sb.Append("  ").AppendLine(oldLines[i]);
        }

        if (linesUsed >= maxHunkLines)
        {
            sb.AppendLine(DiffTruncatedSentinel);
        }

        return sb.ToString().TrimEnd();
    }

    private static string GenerateContentDiff(string content, int maxLines)
    {
        string[] lines = SplitLines(content);
        var sb = new StringBuilder();
        int count = 0;
        foreach (string line in lines)
        {
            if (count >= maxLines)
            {
                sb.AppendLine(ContentTruncatedSentinel);
                break;
            }

            sb.Append("+ ").AppendLine(line);
            count++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string TruncateLines(string text, int maxLines)
    {
        string[] lines = SplitLines(text);
        if (lines.Length <= maxLines)
        {
            return text;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < maxLines; i++)
        {
            sb.AppendLine(lines[i]);
        }

        sb.AppendLine(DiffTruncatedSentinel);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Span-based line split: one pass with <c>\r\n</c> folded as a
    /// single break. Drops the two full-text <c>Replace</c> copies the old
    /// chain needed — same accounting as the PatchTool/HunkParser twins
    /// (#1134 slice 1), including the trailing <c>""</c> Split numbering
    /// relies on. A lone <c>\r</c> is a break here exactly as the old second
    /// Replace made it.</summary>
    private static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        ReadOnlySpan<char> rest = text.AsSpan();
        while (true)
        {
            int nl = rest.IndexOfAny('\r', '\n');
            if (nl < 0)
            {
                lines.Add(rest.ToString());
                return lines.ToArray();
            }

            lines.Add(rest.Slice(0, nl).ToString());
            int next = nl + 1;
            if (rest[nl] == '\r' && next < rest.Length && rest[next] == '\n')
            {
                next++;
            }

            rest = rest.Slice(next);
        }
    }
}
