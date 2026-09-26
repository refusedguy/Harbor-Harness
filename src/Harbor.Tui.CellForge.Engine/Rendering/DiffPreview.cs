using System.Text;
using System.Text.Json;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// CellForge port of the Avalonia <c>DiffPreviewHelper</c>
/// (<c>apps/Harbor.App.Avalonia/Services/DiffPreviewHelper.cs</c>, CF-E-011):
/// builds inline diff previews for the <c>edit</c> / <c>write</c> / <c>patch</c>
/// tool cards. BCL-only (<c>string</c> / <c>StringBuilder</c> /
/// <c>System.Text.Json</c> DOM) — no Avalonia, no reflection, AOT-clean.
/// </summary>
public static class DiffPreview
{
    /// <summary>Visible preview budget — mirrors <c>HdsDiffCompact.MaxLines</c>.</summary>
    public const int MaxPreviewLines = 6;

    /// <summary>Full-diff budget backing the expand path.</summary>
    public const int MaxFullDiffLines = 80;

    /// <summary>Sentinel when old/new share all lines (whitespace-only mid-line change).</summary>
    public const string NoLineDiffSentinel = "(no line-level diff; same lines / whitespace-only mid-line change)";

    /// <summary>Overflow marker for context diffs and patch passthrough.</summary>
    public const string DiffTruncatedSentinel = "… diff truncated";

    /// <summary>Overflow marker for whole-content (<c>write</c>) diffs.</summary>
    public const string ContentTruncatedSentinel = "… truncated";

    /// <summary>Placeholder when no file/path-like string field is found in args.</summary>
    public const string UnknownPath = "<unknown>";

    /// <summary>
    /// Extracts an inline diff preview for diff-capable tools
    /// (<c>edit</c>, <c>write</c>, <c>patch</c>). Other tools return
    /// <c>IsDiffTool: false</c> with null payloads. <paramref name="resultText"/>
    /// is accepted for signature parity with the Avalonia source and reserved
    /// for future use (the preview is derived from args alone).
    /// Malformed args JSON degrades gracefully to "not a diff tool".
    /// </summary>
    public static (bool IsDiffTool, string? FilePath, string? Preview, string? FullDiff) ExtractDiff(
        string toolName, string argsJson, string? resultText)
    {
        if (toolName != "edit" && toolName != "write" && toolName != "patch")
            return (false, null, null, null);

        string? filePath = ExtractFilePath(argsJson);

        if (toolName == "edit")
        {
            string? oldString = null;
            string? newString = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("oldString", out var osEl) && osEl.ValueKind == JsonValueKind.String)
                    oldString = osEl.GetString();
                if (doc.RootElement.TryGetProperty("newString", out var nsEl) && nsEl.ValueKind == JsonValueKind.String)
                    newString = nsEl.GetString();
            }
            catch (JsonException) { /* Malformed JSON payload — diff preview gracefully degrades to raw text. */ }

            if (!string.IsNullOrEmpty(oldString) && newString != null)
            {
                string fullDiff = GenerateContextDiff(oldString, newString, MaxFullDiffLines);
                string preview = GenerateContextDiff(oldString, newString, MaxPreviewLines);
                return (true, filePath, preview, fullDiff);
            }
        }
        else if (toolName == "write")
        {
            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                    content = cEl.GetString();
            }
            catch (JsonException) { /* Malformed JSON payload — diff preview gracefully degrades to raw text. */ }

            if (!string.IsNullOrEmpty(content))
            {
                string fullDiff = GenerateContentDiff(content, MaxFullDiffLines);
                string preview = GenerateContentDiff(content, MaxPreviewLines);
                return (true, filePath, preview, fullDiff);
            }
        }
        else if (toolName == "patch")
        {
            string? patch = null;
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("patch", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                    patch = pEl.GetString();
            }
            catch (JsonException) { /* Malformed JSON payload — diff preview gracefully degrades to raw text. */ }

            if (!string.IsNullOrEmpty(patch))
            {
                string fullDiff = patch;
                string preview = TruncateLines(patch, MaxPreviewLines);
                return (true, filePath, preview, fullDiff);
            }
        }

        return (false, null, null, null);
    }

    /// <summary>
    /// Returns the first string-valued field whose name contains
    /// <c>file</c> or <c>path</c> (case-insensitive), else
    /// <see cref="UnknownPath"/>. Malformed/empty JSON also yields
    /// <see cref="UnknownPath"/>.
    /// </summary>
    public static string ExtractFilePath(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return UnknownPath;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    (prop.Name.Contains("file", StringComparison.OrdinalIgnoreCase)
                     || prop.Name.Contains("path", StringComparison.OrdinalIgnoreCase)))
                {
                    return prop.Value.GetString() ?? UnknownPath;
                }
            }
        }
        catch (JsonException) { /* Malformed JSON payload — diff preview gracefully degrades to raw text. */ }
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
            prefix++;

        int oSuffix = oLen - 1;
        int nSuffix = nLen - 1;
        while (oSuffix >= prefix && nSuffix >= prefix && oldLines[oSuffix] == newLines[nSuffix])
        {
            oSuffix--;
            nSuffix--;
        }

        if (prefix > oSuffix && prefix > nSuffix)
            return NoLineDiffSentinel;

        var sb = new StringBuilder();

        const int ctx = 2;
        int fromOld = Math.Max(0, prefix - ctx);
        int toOld = Math.Min(oLen - 1, oSuffix + ctx);

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
            sb.AppendLine(DiffTruncatedSentinel);

        return sb.ToString().TrimEnd();
    }

    private static string GenerateContentDiff(string content, int maxLines)
    {
        var lines = SplitLines(content);
        var sb = new StringBuilder();
        int count = 0;
        foreach (var line in lines)
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
        var lines = SplitLines(text);
        if (lines.Length <= maxLines)
            return text;

        var sb = new StringBuilder();
        for (int i = 0; i < maxLines; i++)
            sb.AppendLine(lines[i]);
        sb.AppendLine(DiffTruncatedSentinel);
        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }
}
