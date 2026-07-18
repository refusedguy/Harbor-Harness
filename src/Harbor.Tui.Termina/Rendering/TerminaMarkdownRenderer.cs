using System.Text;
using System.Text.RegularExpressions;
using Harbor.Terminal.Abstractions.Rendering;
using Harbor.Ui.Framework.State;
using Termina.Terminal;

namespace Harbor.Tui.Termina.Rendering;

/// <summary>
///     Renders a markdown string into Termina-colored ANSI text. Mirrors the
///     SpectreTui <c>ChatMarkdown</c> behaviour (headings, bold, italic, inline
///     code, bullet/numbered lists, horizontal rule) and adds GFM table rendering
///     via the shared <see cref="GfmTableParser" /> / <see cref="GfmTableFormatter" />.
/// </summary>
public static class TerminaMarkdownRenderer
{
    private static readonly Regex HeadingRegex =
        new(@"^\s{0,3}(#{1,3})\s+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Render the given body text into a sequence of display lines, each
    ///     colorized via ANSI. Tables are expanded inline so the transcript
    ///     stays a single linear flow.
    /// </summary>
    public static IReadOnlyList<string> RenderBody(ChatRole role, string content, int maxWidth = 0)
    {
        var color = TerminaColorMapper.ToColor(role);
        bool md = TerminaColorMapper.SupportsMarkdown(role);
        var lines = (content ?? string.Empty).Replace("\\n", "\n", StringComparison.Ordinal).Split('\n');
        var outp = new List<string>(lines.Length + 4);

        int i = 0;
        while (i < lines.Length)
        {
            if (md && GfmTableParser.IsTableStart(lines, i))
            {
                if (GfmTableParser.TryParse(lines, i, out var table, out int next))
                {
                    foreach (var row in GfmTableFormatter.Format(table, maxWidth))
                        outp.Add(Ansi(Color.Gray, row));
                    i = next;
                    continue;
                }
            }

            outp.Add(md
                ? Ansi(color, RenderInline(lines[i]))
                : Ansi(color, lines[i]));
            i++;
        }

        return outp;
    }

    /// <summary>Render the role header band: <c>─ user ─</c>.</summary>
    public static string RenderHeader(ChatRole role)
    {
        var label = TerminaColorMapper.ToLabel(role);
        var color = TerminaColorMapper.ToColor(role);
        return $"{Ansi(Color.DarkGray, "─ ")}{Ansi(color, label)}{Ansi(Color.DarkGray, " ─")}";
    }

    /// <summary>Inline markdown → ANSI string. Headings / lists / rules are line-level.</summary>
    public static string RenderInline(string text)
    {
        if (text.Length > 4096)
            text = text[..4096];

        var heading = HeadingRegex.Match(text);
        if (heading.Success)
            return Ansi(Color.Yellow, heading.Groups[2].Value, bold: true);

        var trimmed = text.Trim();
        if (trimmed is "---" or "***" or "___")
            return Ansi(Color.DarkGray, new string('─', 12));

        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
            return $"{Ansi(Color.DarkGray, "• ")}{Inline(text[2..])}";

        if (text.Length > 2 && char.IsDigit(text[0]))
        {
            int dot = text.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3)
                return $"{Ansi(Color.DarkGray, text[..(dot + 1)] + " ")}{Inline(text[(dot + 2)..])}";
        }

        return Inline(text);
    }

    private static string Inline(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            // Inline code `…`
            if (text[i] == '`' && i + 1 < text.Length && text[i + 1] != '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    sb.Append(Ansi(Color.Magenta, text.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                    continue;
                }
            }

            // Bold **…** or __…__
            if ((text[i] == '*' || text[i] == '_') && i + 1 < text.Length && text[i + 1] == text[i])
            {
                char c = text[i];
                int end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    sb.Append(Ansi(Color.White, text.Substring(i + 2, end - i - 2), bold: true));
                    i = end + 2;
                    continue;
                }
            }

            // Italic *…* or _…_
            if (text[i] is '*' or '_')
            {
                char c = text[i];
                int end = text.IndexOf(c, i + 1);
                if (end > i && (end == text.Length - 1 || text[end + 1] != c))
                {
                    sb.Append(Ansi(Color.Gray, text.Substring(i + 1, end - i - 1), italic: true));
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Wrap text in a 24-bit ANSI sequence for the given Termina <see cref="Color" />.</summary>
    public static string Ansi(Color color, string text, bool bold = false, bool italic = false)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        string prefix = $"\x1b[38;2;{color.R};{color.G};{color.B}m";
        string style = bold ? "\x1b[1m" : italic ? "\x1b[3m" : string.Empty;
        return $"{prefix}{style}{text}\x1b[0m";
    }
}
