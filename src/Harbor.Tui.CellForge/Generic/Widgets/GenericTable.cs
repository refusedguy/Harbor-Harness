namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Per-column alignment for <see cref="GenericTable"/>.</summary>
public enum GenericColumnAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Generic string-matrix table over cells (Termina <c>Grid</c> slot, epic
/// #1195): box-drawing frame, per-column alignment and shrink-to-fit widths
/// resolved in cell space (wide-rune aware). The chat GFM table stays the
/// markdown primitive; this is the neutral container future generic screens
/// (diagnostics, model lists, eval reports) paint without touching chat.
/// Height is width-independent: columns truncate with an ellipsis, never wrap.
/// </summary>
public sealed class GenericTable
{
    private const int MinCell = 3;

    private const char H = '─';
    private const char V = '│';
    private const char Tl = '┌';
    private const char Tr = '┐';
    private const char Bl = '└';
    private const char Br = '┘';
    private const char Lt = '├';
    private const char Rt = '┤';
    private const char Tp = '┬';
    private const char Bt = '┴';
    private const char Md = '┼';

    private readonly List<string> _headers = new();
    private readonly List<string[]> _rows = new();
    private List<GenericColumnAlign> _aligns = new();

    /// <summary>Headers in column order.</summary>
    public IReadOnlyList<string> Headers => _headers;

    /// <summary>Body rows (each padded/truncated to <see cref="Columns"/> on paint).</summary>
    public IReadOnlyList<string[]> Rows => _rows;

    /// <summary>Column count (header width).</summary>
    public int Columns => _headers.Count;

    /// <summary>Selected body row (-1 = none); painted reversed when set.</summary>
    public int SelectedRow { get; set; } = -1;

    /// <summary>Replaces the headers (copied; null entries coerce to empty).</summary>
    public void SetHeaders(IReadOnlyList<string> headers, IReadOnlyList<GenericColumnAlign>? aligns = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers.Clear();
        for (int i = 0; i < headers.Count; i++)
        {
            _headers.Add(headers[i] ?? string.Empty);
        }

        _aligns = new List<GenericColumnAlign>(_headers.Count);
        for (int i = 0; i < _headers.Count; i++)
        {
            _aligns.Add(aligns is not null && i < aligns.Count ? aligns[i] : GenericColumnAlign.Left);
        }
    }

    /// <summary>Appends a body row (copied; null entries coerce to empty).</summary>
    public void AddRow(IReadOnlyList<string> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = new string[Math.Max(1, _headers.Count)];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = i < cells.Count ? cells[i] ?? string.Empty : string.Empty;
        }

        _rows.Add(row);
    }

    /// <summary>Clears body rows (headers survive).</summary>
    public void ClearRows() => _rows.Clear();

    /// <summary>Painted height for <paramref name="rowCount"/> body rows: frame + header + rows.</summary>
    public static int HeightFor(int rowCount) => Math.Max(0, rowCount) + 4;

    /// <summary>Total painted height for the current rows.</summary>
    public int DesiredHeight => HeightFor(_rows.Count);

    /// <summary>Column content widths fitted into <paramref name="width"/> (pure, for tests).</summary>
    public int[] MeasureWidths(int width)
    {
        if (_headers.Count == 0 || width <= 0)
        {
            return [];
        }

        int cols = _headers.Count;
        var natural = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            natural[c] = Math.Max(MinCell, UnicodeWidth.Width(_headers[c].AsSpan()));
            for (int r = 0; r < _rows.Count; r++)
            {
                string cell = c < _rows[r].Length ? _rows[r][c] : string.Empty;
                natural[c] = Math.Max(natural[c], UnicodeWidth.Width(cell.AsSpan()));
            }
        }

        int borders = cols + 1;
        int padding = cols * 2;
        int avail = width - borders - padding;
        if (avail < cols * MinCell)
        {
            var min = new int[cols];
            Array.Fill(min, MinCell);
            return min;
        }

        int total = 0;
        for (int c = 0; c < cols; c++)
        {
            total += natural[c];
        }

        if (total <= avail)
        {
            return natural;
        }

        var fitted = (int[])natural.Clone();
        int over = total - avail;
        while (over > 0)
        {
            int widest = -1;
            for (int c = 0; c < cols; c++)
            {
                if (fitted[c] > MinCell && (widest < 0 || fitted[c] > fitted[widest]))
                {
                    widest = c;
                }
            }

            if (widest < 0)
            {
                break;
            }

            fitted[widest]--;
            over--;
        }

        return fitted;
    }

    /// <summary>Paints the frame, header and body rows at <paramref name="rect"/>.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _headers.Count == 0)
        {
            return;
        }

        int[] widths = MeasureWidths(rect.Width);
        var borderStyle = new CellStyle(ChatPalette.Border);
        var headerStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var bodyStyle = new CellStyle(ChatPalette.Text);
        var selectedStyle = new CellStyle(ChatPalette.Text, attrs: StyleAttr.Reverse);

        int y = rect.Y;
        PaintRule(buffer, rect.X, y, widths, Tl, Tr, Tp, borderStyle);
        y++;
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        PaintRow(buffer, rect.X, y, widths, _headers, headerStyle, borderStyle);
        y++;
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        PaintRule(buffer, rect.X, y, widths, Lt, Rt, Md, borderStyle);
        y++;

        for (int r = 0; r < _rows.Count && y < rect.Y + rect.Height - 1; r++, y++)
        {
            var cells = new List<string>(_rows[r]);
            bool selected = r == SelectedRow;
            PaintRow(buffer, rect.X, y, widths, cells, selected ? selectedStyle : bodyStyle, borderStyle);
        }

        if (y < rect.Y + rect.Height)
        {
            PaintRule(buffer, rect.X, y, widths, Bl, Br, Bt, borderStyle);
        }
    }

    private void PaintRule(ScreenBuffer buffer, int x, int y, int[] widths, char left, char right, char mid, CellStyle style)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(left);
        for (int c = 0; c < widths.Length; c++)
        {
            sb.Append(H, widths[c] + 2);
            sb.Append(c == widths.Length - 1 ? right : mid);
        }

        buffer.SetText(x, y, sb.ToString(), style);
    }

    private void PaintRow(
        ScreenBuffer buffer, int x, int y, int[] widths,
        IReadOnlyList<string> cells, CellStyle style, CellStyle borderStyle)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(V);
        for (int c = 0; c < widths.Length; c++)
        {
            sb.Append(' ');
            string text = c < cells.Count ? cells[c] ?? string.Empty : string.Empty;
            sb.Append(Align(Fit(text, widths[c]), widths[c], _aligns.Count > c ? _aligns[c] : GenericColumnAlign.Left));
            sb.Append(' ');
            sb.Append(V);
        }

        string row = sb.ToString();
        buffer.SetText(x, y, row, style);

        // Re-tint the border columns so the frame reads as chrome, not content.
        int cx = x;
        buffer.SetStyleAt(cx, y, borderStyle);
        cx++;
        for (int c = 0; c < widths.Length; c++)
        {
            cx += widths[c] + 2;
            buffer.SetStyleAt(cx, y, borderStyle);
            cx++;
        }
    }

    private static string Fit(string text, int width)
    {
        if (UnicodeWidth.Width(text.AsSpan()) <= width)
        {
            return text;
        }

        if (width <= 1)
        {
            return width == 1 ? "…" : string.Empty;
        }

        var span = text.AsSpan();
        int cells = 0;
        int i = 0;
        while (i < span.Length)
        {
            System.Text.Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int w = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + w > width - 1)
            {
                break;
            }

            cells += w;
            i += consumed;
        }

        return text[..i] + "…";
    }

    private static string Align(string text, int width, GenericColumnAlign align)
    {
        int pad = width - UnicodeWidth.Width(text.AsSpan());
        if (pad <= 0)
        {
            return text;
        }

        return align switch
        {
            GenericColumnAlign.Right => new string(' ', pad) + text,
            GenericColumnAlign.Center => new string(' ', pad / 2) + text + new string(' ', pad - (pad / 2)),
            _ => text + new string(' ', pad),
        };
    }
}
