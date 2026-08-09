using System.Text;
using System.Text.Json;
using Harbor.Ui.Framework.ViewModels;

namespace Harbor.App.Avalonia.Services;

public static class DiffPreviewHelper
{
    private const int MaxPreviewLines = 6;
    private const int MaxFullDiffLines = 80;

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

    private static string ExtractFilePath(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return "<unknown>";

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    (prop.Name.Contains("file", StringComparison.OrdinalIgnoreCase)
                     || prop.Name.Contains("path", StringComparison.OrdinalIgnoreCase)))
                {
                    return prop.Value.GetString() ?? "<unknown>";
                }
            }
        }
        catch (JsonException) { /* Malformed JSON payload — diff preview gracefully degrades to raw text. */ }
        return "<unknown>";
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
            return "(no line-level diff; same lines / whitespace-only mid-line change)";

        var sb = new StringBuilder();

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

    private static string GenerateContentDiff(string content, int maxLines)
    {
        var lines = SplitLines(content);
        var sb = new StringBuilder();
        int count = 0;
        foreach (var line in lines)
        {
            if (count >= maxLines)
            {
                sb.AppendLine("… truncated");
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
        sb.AppendLine("… diff truncated");
        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }
}
