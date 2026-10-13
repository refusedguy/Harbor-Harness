namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Title placement on a <see cref="RuleView" /> row.</summary>
public enum RuleJustify : byte
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// Cell-native horizontal rule with an optional title (Spectre <c>Rule</c>
/// pattern): a <c>─</c> run in <see cref="PanelChrome.BorderStyle" /> with the
/// title in <see cref="PanelChrome.TitleStyle" />, padded by single spaces.
/// Justification places the title left/center/right; overlong titles are
/// ellipsis-truncated (<c>…</c>) to fit instead of pushing the rule off the
/// row. Empty title paints a plain rule. Bounds-safe (clips to the buffer,
/// never throws on degenerate widths) and allocation-free on paint.
/// </summary>
public static class RuleView
{
    /// <summary>
    /// Paints the rule at row <paramref name="y" /> from <paramref name="x" />
    /// across <paramref name="width" /> cells.
    /// </summary>
    public static void Paint(
        ScreenBuffer buffer,
        int x,
        int y,
        int width,
        string? title = null,
        RuleJustify justify = RuleJustify.Center)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (width <= 0)
        {
            return;
        }

        var ruleStyle = PanelChrome.BorderStyle;
        var titleStyle = PanelChrome.TitleStyle;
        var rule = new Rune(PanelChrome.RuleGlyph);

        if (string.IsNullOrEmpty(title))
        {
            for (int i = 0; i < width; i++)
            {
                buffer.SetRune(x + i, y, rule, in ruleStyle);
            }

            return;
        }

        // Title cell budget: the padded " title " run must leave at least one
        // rule cell on each side when the width allows; tiny widths truncate
        // the title down to the bare row.
        int titleCells = Math.Min(UnicodeWidth.Width(title), Math.Max(0, width - 4));
        string fitted = TruncateTitle(title, titleCells);
        int fittedCells = UnicodeWidth.Width(fitted);
        int padded = fittedCells + 2; // single-space padding each side

        int start = justify switch
        {
            RuleJustify.Left => x + 1,
            RuleJustify.Right => x + width - 1 - padded,
            _ => x + ((width - padded) / 2),
        };
        start = Math.Clamp(start, x, Math.Max(x, (x + width) - padded));

        for (int cx = x; cx < x + width; cx++)
        {
            buffer.SetRune(cx, y, rule, in ruleStyle);
        }

        buffer.SetRune(start, y, new Rune(' '), in ruleStyle);
        if (fitted.Length > 0)
        {
            buffer.SetText(start + 1, y, fitted.AsSpan(), in titleStyle);
        }

        buffer.SetRune(start + 1 + fittedCells, y, new Rune(' '), in ruleStyle);
    }

    /// <summary>
    /// Longest leading run of <paramref name="title" /> fitting in
    /// <paramref name="maxCells" /> cells, with a trailing <c>…</c> when cut.
    /// Pure — shared by paint and by hosts measuring first.
    /// </summary>
    public static string TruncateTitle(string title, int maxCells)
    {
        if (string.IsNullOrEmpty(title) || maxCells <= 0)
        {
            return string.Empty;
        }

        if (UnicodeWidth.Width(title) <= maxCells)
        {
            return title;
        }

        if (maxCells == 1)
        {
            return "…";
        }

        var sb = new System.Text.StringBuilder();
        int w = 0;
        var span = title.AsSpan();
        while (!span.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf16(span, out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int cw = Math.Max(1, UnicodeWidth.Width(rune));
            if (w + cw > maxCells - 1)
            {
                break;
            }

            sb.Append(span[..consumed]);
            w += cw;
            span = span[consumed..];
        }

        sb.Append('…');
        return sb.ToString();
    }
}
