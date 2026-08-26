using System.Globalization;
using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Grid-dump serialization for golden tests: a ScreenBuffer becomes a
/// three-layer text map — cosmetic row art, an exact per-cell
/// <c>col,row: rune style</c> listing of every non-default cell, and the raw
/// escaped ANSI captured by the recording backend. An SVG projection is
/// available for human PR review only (never asserted).
/// </summary>
internal static class GridDump
{
    /// <summary>Cosmetic rendering: one line per row; wide tails collapse.</summary>
    public static string Art(ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.Width == Cell.WSkip)
                {
                    continue; // tail half — glyph already drawn by its lead
                }

                sb.Append(ArtChar(cell.Rune));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string ArtChar(int rune) => rune switch
    {
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFFFD when !char.IsSurrogate((char)rune) => ((char)rune).ToString(),
        _ => "?",
    };

    /// <summary>Exact map: one <c>x,y: rune style width</c> line per non-blank cell.</summary>
    public static string Cells(ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.IsBlankSpace)
                {
                    continue;
                }

                sb.Append(CultureInfo.InvariantCulture, $"{x},{y}: {RuneToken(cell.Rune)} {StyleCode(cell)} {WidthToken(cell)}\n");
            }
        }

        return sb.ToString();
    }

    private static string RuneToken(int rune) =>
        rune >= 0x20 && rune != 0x7F && rune <= 0xFFFF && !char.IsSurrogate((char)rune)
            ? $"'{(char)rune}'"
            : $"U+{rune:X4}";

    private static string WidthToken(Cell cell) => cell.Width switch
    {
        Cell.WSkip => "tail",
        Cell.Wide => "w2",
        _ => "w1",
    };

    /// <summary>Deterministic style code: <c>fg/bg/attrs</c>, e.g. <c>i8/d/B</c>.</summary>
    public static string StyleCode(in Cell cell) => StyleCode(cell.Style);

    public static string StyleCode(in CellStyle style)
    {
        string attrs = style.Attrs == StyleAttr.None
            ? "-"
            : AttrLetters(style.Attrs);
        return $"{ColorCode(style.Fg)}/{ColorCode(style.Bg)}/{attrs}";
    }

    private static string AttrLetters(StyleAttr attrs)
    {
        var sb = new StringBuilder();
        foreach (var (flag, letter) in new[]
                 {
                     (StyleAttr.Bold, 'B'), (StyleAttr.Dim, 'D'), (StyleAttr.Italic, 'I'),
                     (StyleAttr.Underline, 'U'), (StyleAttr.Blink, 'L'), (StyleAttr.Reverse, 'R'),
                     (StyleAttr.Hidden, 'H'), (StyleAttr.Strike, 'S'),
                 })
        {
            if ((attrs & flag) != 0)
            {
                sb.Append(letter);
            }
        }

        return sb.ToString();
    }

    private static string ColorCode(PackedColor color) => color.IsDefault
        ? "d"
        : color.IsRgb ? Rgb(color.RgbChannels.R, color.RgbChannels.G, color.RgbChannels.B) : $"i{color.Index}";

    /// <summary>All captured writes as numbered escaped-ANSI blocks.</summary>
    public static string Frames(RecordingBackend backend)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < backend.Writes.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"## frame {i + 1} ({backend.Writes[i].Length} bytes)\n");
            sb.Append(Escape(Encoding.UTF8.GetString(backend.Writes[i])));
            sb.Append('\n');
        }

        return sb.Length == 0 ? "(no frames)\n" : sb.ToString();
    }

    /// <summary>Control characters rendered visible (\e, \r, \n) — golden-safe.</summary>
    public static string Escape(string ansi) => ansi
        .Replace("\u001B", "\\e")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");

    /// <summary>
    /// Human-viewable projection (PR review aid only — never asserted):
    /// monospace grid with palette-index hues and bold weight mapping.
    /// </summary>
    public static string ToSvg(ScreenBuffer buffer)
    {
        const int cw = 12;
        const int ch = 20;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{buffer.Cols * cw}' height='{buffer.Rows * ch}' font-family='monospace' font-size='{ch - 5}'>\n");
        sb.Append("<rect width='100%' height='100%' fill='#101018'/>\n");

        var body = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.IsBlankSpace || cell.Width == Cell.WSkip || cell.Rune < 0x20)
                {
                    continue;
                }

                string fg = SvgColor(cell.Style.Fg);
                string weight = (cell.Style.Attrs & StyleAttr.Bold) != 0 ? " font-weight='bold'" : "";
                string deco = (cell.Style.Attrs & StyleAttr.Underline) != 0 ? " text-decoration='underline'" : "";
                body.Append(CultureInfo.InvariantCulture,
                    $"<text x='{x * cw}' y='{y * ch + ch - 6}' fill='{fg}' xml:space='preserve'{weight}{deco}>{RuneToken(cell.Rune).Trim('\'')}</text>\n");
            }
        }

        sb.Append(body);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string SvgColor(PackedColor color) => color.IsDefault
        ? "#CCCCCC"
        : color.IsRgb
            ? Rgb(color.RgbChannels.R, color.RgbChannels.G, color.RgbChannels.B)
            : $"hsl({color.Index * 47 % 360},70%,70%)";

    private static string Rgb(byte r, byte g, byte b) => $"rgb({r},{g},{b})";
}
