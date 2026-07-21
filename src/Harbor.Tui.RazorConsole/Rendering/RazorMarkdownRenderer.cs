using System.Text.RegularExpressions;
using Harbor.Terminal.Abstractions.Rendering;
using Harbor.Ui.Framework.State;
namespace Harbor.Tui.RazorConsole.Rendering;
/// <summary>
///     Renders markdown into Spectre markup strings for RazorConsole. Mirrors
///     the SpectreTui ChatMarkdown behaviour (headings, bold, italic, inline
///     code, bullet/numbered lists, horizontal rule) and adds GFM table
///     rendering via the shared GfmTableParser / GfmTableFormatter.
/// </summary>
public static class RazorMarkdownRenderer
{
    private static readonly Regex HeadingRegex =
        new(@"^\s{0,3}(#{1,3})\s+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Render the body text into a sequence of Spectre markup strings.
    ///     Tables are expanded inline. Color is encoded as
    ///     <c>[green]…[/]</c> so the RazorConsole Spectre pipeline can apply
    ///     it directly.
    /// </summary>
    public static IReadOnlyList<string> RenderBody(ChatRole role, string content, int maxWidth = 0)
    {
        bool md = RazorColorMapper.SupportsMarkdown(role);
        string markup = RazorColorMapper.ToMarkup(role);
        string[] lines = (content ?? string.Empty).Replace("\\n", "\n", StringComparison.Ordinal).Split('\n');
        var outp = new List<string>(lines.Length + 4);

        int i = 0;
        while (i < lines.Length)
        {
            if (md && GfmTableParser.IsTableStart(lines, i))
            {
                if (GfmTableParser.TryParse(lines, i, out var table, out int next))
                {
                    foreach (string row in GfmTableFormatter.Format(table, maxWidth))
                        outp.Add($"[grey]{Escape(row)}[/]");
                    i = next;
                    continue;
                }
            }
            outp.Add(md
                ? $"[{markup}]{Escape(RenderInline(lines[i]))}[/]"
                : $"[{markup}]{Escape(lines[i])}[/]");
            i++;
        }
        return outp;
    }

    /// <summary>Render the role header band: <c>─ user ─</c>.</summary>
    public static string RenderHeader(ChatRole role) =>
        $"[grey]─ [/][{RazorColorMapper.ToMarkup(role)}]{RazorColorMapper.ToLabel(role)}[/][grey] ─[/]";

    /// <summary>Inline markdown → plain string (RazorConsole applies color via markup wrapper).</summary>
    public static string RenderInline(string text)
    {
        if (text.Length > 4096)
            text = text[..4096];

        var heading = HeadingRegex.Match(text);
        if (heading.Success)
            return heading.Groups[2].Value;

        string trimmed = text.Trim();
        if (trimmed is "---" or "***" or "___")
            return new string('─', 12);

        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
            return $"• {text[2..]}";

        if (text.Length > 2 && char.IsDigit(text[0]))
        {
            int dot = text.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3)
                return $"{text[..(dot + 1)]} {text[(dot + 2)..]}";
        }

        return text;
    }

    /// <summary>Escape Spectre markup metacharacters (<c>[</c>, <c>]</c>) so user text renders verbatim.</summary>
    public static string Escape(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}
