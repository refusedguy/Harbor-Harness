using System.Text;
using System.Text.RegularExpressions;
using Harbor.Terminal.Abstractions.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Tui.TerminalGui.Rendering;
/// <summary>
///     Renders markdown into Terminal.Gui v2 attribute-ready plain text.
///     Mirrors the SpectreTui ChatMarkdown behaviour (headings, bold, italic,
///     inline code, bullet/numbered lists, horizontal rule) and adds GFM
///     table rendering via the shared GfmTableParser / GfmTableFormatter.
///     Output is plain Unicode (no ANSI) — Terminal.Gui applies color via
///     <c>Attribute</c> on the <c>TextView</c> / <c>Label</c>.
/// </summary>
public static class TerminalGuiMarkdownRenderer
{
    private static readonly Regex HeadingRegex =
        new(@"^\s{0,3}(#{1,3})\s+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Render the body text into a sequence of display lines. Tables are
    ///     expanded inline. Color is applied separately by the view layer
    ///     based on <see cref="TerminalGuiColorMapper.ToColor" />.
    /// </summary>
    public static IReadOnlyList<string> RenderBody(ChatRole role, string content, int maxWidth = 0)
    {
        bool md = TerminalGuiColorMapper.SupportsMarkdown(role);
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
                        outp.Add(row);
                    i = next;
                    continue;
                }
            }
            outp.Add(md ? RenderInline(lines[i]) : lines[i]);
            i++;
        }
        return outp;
    }

    /// <summary>Render the role header band: <c>─ user ─</c>.</summary>
    public static string RenderHeader(ChatRole role) =>
        $"─ {TerminalGuiColorMapper.ToLabel(role)} ─";

    /// <summary>Inline markdown → plain Unicode string (color applied by the view).</summary>
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
            return $"• {StripInline(text[2..])}";

        if (text.Length > 2 && char.IsDigit(text[0]))
        {
            int dot = text.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3)
                return $"{text[..(dot + 1)]} {StripInline(text[(dot + 2)..])}";
        }

        return StripInline(text);
    }

    private static string StripInline(string text)
    {
        // Strip markdown emphasis markers — Terminal.Gui TextView doesn't
        // honor inline ANSI; color is per-Attribute (whole run). Bold/italic
        // would require a separate attribute scheme; leave the inner text
        // intact so the user still sees the content.
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && i + 1 < text.Length && text[i + 1] != '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    sb.Append(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }
            }

            if ((text[i] == '*' || text[i] == '_') && i + 1 < text.Length && text[i + 1] == text[i])
            {
                char c = text[i];
                int end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    sb.Append(text.Substring(i + 2, end - i - 2));
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] is '*' or '_')
            {
                char c = text[i];
                int end = text.IndexOf(c, i + 1);
                if (end > i && (end == text.Length - 1 || text[end + 1] != c))
                {
                    sb.Append(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }
}
