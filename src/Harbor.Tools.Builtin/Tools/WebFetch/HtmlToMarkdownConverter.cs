using System.Net;
using System.Text.RegularExpressions;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Regex-based HTML → markdown converter. No deps. Lossy but agent-friendly.
///     Pure static function, moved verbatim from <see cref="WebFetchTool" />
///     (#95 giant-files backlog, one seam: HtmlToMarkdown + StripTags).
/// </summary>
public static class HtmlToMarkdownConverter
{
    /// <summary>
    ///     Converts an HTML fragment to agent-friendly markdown (tags stripped,
    ///     headings/links/code preserved as markdown). Returns
    ///     <see cref="string.Empty" /> for null or empty input.
    /// </summary>
    public static string Convert(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        string s = html.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var codeBlocks = new List<string>(4);
        s = Regex.Replace(s, @"<pre\b[^>]*>(.*?)</pre>", m =>
        {
            string code = StripTags(m.Groups[1].Value);
            code = WebUtility.HtmlDecode(code).Trim();
            string fenced = "```\n" + code + "\n```";
            codeBlocks.Add(fenced);
            return "\u0000CODE" + (codeBlocks.Count - 1) + "\u0000";
        }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<code\b[^>]*>(.*?)</code>",
            m => "`" + StripTags(m.Groups[1].Value).Trim() + "`",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int level = 1; level <= 6; level++)
        {
            string h = "h" + level;
            string prefix = new string('#', level) + " ";
            s = Regex.Replace(s, $"<{h}\\b[^>]*>(.*?)</{h}>",
                m => "\n\n" + prefix + StripTags(m.Groups[1].Value).Trim() + "\n\n",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        s = Regex.Replace(s, @"<a\b[^>]*href=[""']([^""']+)[""'][^>]*>(.*?)</a>",
            m => "[" + StripTags(m.Groups[2].Value).Trim() + "](" + m.Groups[1].Value + ")",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</li>", "", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"</?(p|div|section|article|header|footer|main|nav|aside)\b[^>]*>",
            "\n\n", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<!--.*?-->", "", RegexOptions.Singleline);

        s = StripTags(s);

        for (int i = 0; i < codeBlocks.Count; i++)
            s = s.Replace("\u0000CODE" + i + "\u0000", codeBlocks[i]);

        s = WebUtility.HtmlDecode(s);

        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        return s.Trim();
    }

    private static string StripTags(string s)
        => Regex.Replace(s, @"<[^>]+>", string.Empty);
}
