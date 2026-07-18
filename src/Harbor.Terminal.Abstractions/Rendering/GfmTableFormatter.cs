using System.Text;
namespace Harbor.Terminal.Abstractions.Rendering;
/// <summary>
///     Renders a <see cref="GfmTable" /> to a plain Unicode-grid of display
///     strings (one <see cref="string" /> per row). Framework-free: contains no
///     <c>TextLine</c> / color / Spectre types. Renderers map each string into
///     their own widget model. Each produced row is one display row in a
///     virtualized transcript (no extra widget, no row-model break).
///
///     Geometry contract: every row (rule and data) has the SAME visible width.
///     A column is drawn as <c>┃ space cell space ┃</c> — i.e. each cell consumes
///     <c>width + 2</c> display columns (padding). Horizontal rules use the same
///     <c>width + 2</c> dashes so the grid never desyncs. A row is never emitted
///     wider than the maxWidth passed to <see cref="Format(GfmTable, int)" />:
///     after building, each line is hard-truncated so the terminal never wraps a
///     table row (which would destroy the box drawing).
/// </summary>
public static class GfmTableFormatter
{
    private const int MinCell = 3;
    private const int Pad = 1; // single space on each side of a cell
    // Hard cap so a single runaway cell (e.g. 100k chars) cannot make the
    // formatter grind; truncation to the visible width happens later anyway.
    private const int MaxCell = 256;

    private const char H = '─';
    private const char V = '│';
    private const char X = '┼';
    private const char Lt = '├';
    private const char Rt = '┤';
    private const char Tl = '┌';
    private const char Tr = '┐';
    private const char Bl = '└';
    private const char Br = '┘';

    public static IReadOnlyList<string> Format(GfmTable table, int maxWidth = 0)
    {
        int cols = table.Headers.Count;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int w = CellWidth(table.Headers[c]);
            foreach (var row in table.Rows)
                w = Math.Max(w, CellWidth(row[c]));
            widths[c] = w;
        }

        if (maxWidth > 4)
        {
            // Full visible width of one row including borders + padding.
            int decor = (cols + 1) + 2 * cols;
            int used = widths.Sum() + decor;
            if (used > maxWidth)
                ShrinkToFit(widths, maxWidth - decor);
        }

        var outp = new List<string>(table.Rows.Count + 4);
        outp.Add(HRule(widths, Tl, Tr, X));
        outp.Add(DataRow(table.Headers, widths, table.Alignments));
        outp.Add(HRule(widths, Lt, Rt, X));
        foreach (var row in table.Rows)
            outp.Add(DataRow(row, widths, table.Alignments));
        outp.Add(HRule(widths, Bl, Br, X));

        // Never emit a row wider than the panel; wrap would kill the grid.
        if (maxWidth > 0)
            for (int k = 0; k < outp.Count; k++)
                if (DispWidth(outp[k]) > maxWidth)
                    outp[k] = HardTruncate(outp[k], maxWidth);

        return outp;
    }

    private static int CellWidth(string cell)
        => Math.Max(MinCell, Math.Min(DispWidth(cell), MaxCell));

    private static void ShrinkToFit(int[] widths, int budget)
    {
        int total = widths.Sum();
        if (total <= budget)
            return;

        while (total > budget)
        {
            // Narrow the widest column still above the minimum floor.
            int idx = -1;
            for (int c = 0; c < widths.Length; c++)
                if (widths[c] > MinCell && (idx < 0 || widths[c] > widths[idx]))
                    idx = c;
            if (idx < 0)
                break; // every column pinned at minimum; cannot fit
            // Cut the whole excess in one pass (not one char at a time).
            int over = total - budget;
            int can = widths[idx] - MinCell;
            int cut = Math.Min(can, Math.Max(1, over));
            widths[idx] -= cut;
            total -= cut;
        }
    }

    private static string HRule(int[] widths, char left, char right, char mid)
    {
        var sb = new StringBuilder();
        sb.Append(left);
        for (int c = 0; c < widths.Length; c++)
        {
            sb.Append(H, widths[c] + 2 * Pad);
            sb.Append(c < widths.Length - 1 ? mid : right);
        }
        return sb.ToString();
    }

    private static string DataRow(
        IReadOnlyList<string> cells, int[] widths, IReadOnlyList<GfmAlign> aligns)
    {
        var sb = new StringBuilder();
        sb.Append(V).Append(' ');
        for (int c = 0; c < widths.Length; c++)
        {
            string text = c < cells.Count ? cells[c] : string.Empty;
            sb.Append(FormatCell(text, widths[c], c < aligns.Count ? aligns[c] : GfmAlign.Left));
            sb.Append(c < widths.Length - 1 ? " " + V + " " : " " + V);
        }
        return sb.ToString();
    }

    private static string FormatCell(string text, int width, GfmAlign align)
    {
        int dw = DispWidth(text);
        if (dw > width)
            text = HardTruncate(text, width);
        int pad = width - DispWidth(text);
        if (pad <= 0)
            return text;
        return align switch
        {
            GfmAlign.Right => new string(' ', pad) + text,
            GfmAlign.Center => new string(' ', pad / 2) + text + new string(' ', pad - pad / 2),
            _ => text + new string(' ', pad)
        };
    }

    /// <summary>
    ///     Display width aware of East-Asian wide/fullwidth chars (e.g. Cyrillic
    ///     and CJK render wider than ASCII in terminals). A rough half-width
    ///     approximation: anything in the wide ranges counts as 2 columns.
    /// </summary>
    private static int DispWidth(string text)
    {
        int w = 0;
        foreach (var ch in text)
            w += IsWide(ch) ? 2 : 1;
        return w;
    }

    private static bool IsWide(char c)
    {
        // Covers common wide ranges (CJK, Hangul, fullwidth forms). Cyrillic is
        // NOT wide in terminals; this stays conservative to avoid under-sizing.
        return (c >= 0x1100 && c <= 0x115F) || // Hangul Jamo
               (c >= 0x2E80 && c <= 0x303E) ||
               (c >= 0x3041 && c <= 0x33FF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0xA000 && c <= 0xA4CF) ||
               (c >= 0xAC00 && c <= 0xD7A3) ||
               (c >= 0xF900 && c <= 0xFAFF) ||
               (c >= 0xFE30 && c <= 0xFE4F) ||
               (c >= 0xFF00 && c <= 0xFF60) ||
               (c >= 0xFFE0 && c <= 0xFFE6);
    }

    private static string HardTruncate(string text, int width)
    {
        // Cut to fit `width` display columns, appending an ellipsis.
        var sb = new StringBuilder();
        int w = 0;
        foreach (var ch in text)
        {
            int cw = IsWide(ch) ? 2 : 1;
            if (w + cw > width - 1) // leave room for the ellipsis
                break;
            sb.Append(ch);
            w += cw;
        }
        sb.Append('…');
        return sb.ToString();
    }
}
