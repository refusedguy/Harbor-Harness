namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Cell-width-aware wrapping for log text. Pure and allocation-free: segments
/// are slices of the caller's span, so layout and paint share one set of row
/// boundaries without building intermediate strings. Newlines are hard row
/// breaks; with wrapping off every logical line is exactly one row.
/// </summary>
public static class LogWrap
{
    /// <summary>Number of logical (newline-separated) lines in the text.</summary>
    public static int LogicalLineCount(ReadOnlySpan<char> text)
    {
        int count = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Rows the text occupies at <paramref name="width"/> cells. With wrapping
    /// off this is the logical line count; with it on, each logical line
    /// contributes its wrapped segment count (at least one).
    /// </summary>
    public static int RowCount(ReadOnlySpan<char> text, int width, bool wrap)
    {
        int w = Math.Max(1, width);
        int rows = 0;
        var rest = text;
        while (true)
        {
            int nl = rest.IndexOf('\n');
            var logical = (nl < 0 ? rest : rest[..nl]).TrimEnd('\r');
            rows += wrap ? SegmentCount(logical, w) : 1;
            if (nl < 0)
            {
                return rows;
            }

            rest = rest[(nl + 1)..];
        }
    }

    /// <summary>
    /// The wrapped segment at <paramref name="rowIndex"/> within the text.
    /// Returns an empty span when the index is out of range.
    /// </summary>
    public static ReadOnlySpan<char> Segment(ReadOnlySpan<char> text, int width, bool wrap, int rowIndex)
    {
        if (rowIndex < 0)
        {
            return default;
        }

        int w = Math.Max(1, width);
        int row = 0;
        var rest = text;
        while (true)
        {
            int nl = rest.IndexOf('\n');
            var logical = (nl < 0 ? rest : rest[..nl]).TrimEnd('\r');
            int segments = wrap ? SegmentCount(logical, w) : 1;
            if (row + segments > rowIndex)
            {
                return wrap ? SegmentAt(logical, w, rowIndex - row) : logical;
            }

            row += segments;
            if (nl < 0)
            {
                return default;
            }

            rest = rest[(nl + 1)..];
        }
    }

    /// <summary>
    /// Longest prefix of <paramref name="text"/> that fits in
    /// <paramref name="width"/> display cells. Used by painting to keep a
    /// segment inside its clip rect even when the layout width is wider.
    /// </summary>
    public static ReadOnlySpan<char> Fit(ReadOnlySpan<char> text, int width)
    {
        int w = Math.Max(0, width);
        int cells = 0;
        int i = 0;
        while (i < text.Length)
        {
            System.Text.Rune.DecodeFromUtf16(text[i..], out var rune, out int consumed);
            if (consumed <= 0)
            {
                consumed = 1;
            }

            int runeWidth = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + runeWidth > w)
            {
                break;
            }

            cells += runeWidth;
            i += consumed;
        }

        return text[..i];
    }

    private static int SegmentCount(ReadOnlySpan<char> logical, int width)
    {
        int segments = 1;
        int cells = 0;
        int i = 0;
        while (i < logical.Length)
        {
            System.Text.Rune.DecodeFromUtf16(logical[i..], out var rune, out int consumed);
            if (consumed <= 0)
            {
                consumed = 1;
            }

            int runeWidth = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells > 0 && cells + runeWidth > width)
            {
                segments++;
                cells = 0;
            }

            cells += runeWidth;
            i += consumed;
        }

        return segments;
    }

    private static ReadOnlySpan<char> SegmentAt(ReadOnlySpan<char> logical, int width, int index)
    {
        int segment = 0;
        int cells = 0;
        int start = 0;
        int i = 0;
        while (i < logical.Length)
        {
            System.Text.Rune.DecodeFromUtf16(logical[i..], out var rune, out int consumed);
            if (consumed <= 0)
            {
                consumed = 1;
            }

            int runeWidth = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells > 0 && cells + runeWidth > width)
            {
                if (segment == index)
                {
                    return logical[start..i];
                }

                segment++;
                start = i;
                cells = 0;
            }

            cells += runeWidth;
            i += consumed;
        }

        return segment == index ? logical[start..] : default;
    }
}
