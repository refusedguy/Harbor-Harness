using System.Text;
using Harbor.Terminal.Abstractions.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Cell-native GFM table block over the strings-only
/// <see cref="GfmTableParser" /> model: box-drawing frame, per-column
/// <see cref="GfmAlign" /> alignment and width shrink-to-fit are resolved in
/// cell space (wide-rune aware via <see cref="UnicodeWidth" />) and painted
/// with per-segment <see cref="ScreenBuffer.SetText" /> calls — borders dim,
/// header accent-bold, body plain — instead of preformatted strings.
/// Height is width-independent (columns truncate, never wrap):
/// top rule + header + mid rule + body rows + bottom rule.
/// Collapsible per the <c>ICollapsibleChatBlock</c> mixin ([UX2] #262):
/// tables over <see cref="MaxBodyLines"/> (universal 10) total rows render
/// collapsed — top slice + <c>"... (N hidden)"</c> tail + bottom rule, so the
/// frame survives the cut; shorter tables paint fully, so the
/// default-collapsed state stays byte-identical to the pre-collapse layout.
/// Feed Enter/click/space toggles via <see cref="ToggleExpanded"/>.
/// </summary>
public sealed class TableBlock : ICollapsibleChatBlock
{
    private const int MinCell = 3;
    private const int Pad = 1;
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

    private readonly GfmTable _table;
    private readonly int _budgetBytes;

    // ENG10 #282: one-shot layout cache (width-keyed, same discipline as
    // AssistantMarkdownBlock._lines). Table content is immutable once parsed,
    // so widths / rule strings / fitted cells are rebuilt only when the paint
    // width or row count changes — steady-state Paint only slices spans.
    private int[]? _layoutWidths;
    private string[]? _layoutRules; // top, mid, bottom
    private string[]? _layoutCells; // (1 header + N body rows) × Columns fitted cells
    private int _layoutWidth = -1;
    private int _layoutRows = -1;

    public TableBlock(GfmTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Headers.Count == 0)
        {
            throw new ArgumentException("Table must have at least one column.", nameof(table));
        }

        _table = table;
        int bytes = 64;
        bytes += CellsBytes(table.Headers);
        foreach (var row in table.Rows)
        {
            bytes += CellsBytes(row);
        }

        _budgetBytes = bytes;
    }

    public string Kind => "table";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => _budgetBytes;

    /// <summary>Parsed table (strings-only model from <see cref="GfmTableParser" />).</summary>
    public GfmTable Table => _table;

    /// <summary>Column count (header width).</summary>
    public int Columns => _table.Headers.Count;

    /// <summary>
    /// Collapsed-body row budget ([UX2] #262: universal 10-line policy).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; } = ICollapsibleChatBlock.DefaultUniversalBodyLines;

    /// <summary>
    /// Whether the block is expanded (feed Enter/click/space toggles via
    /// <see cref="ToggleExpanded"/>). Defaults to <c>false</c> so tables over
    /// the budget collapse structurally — no ingestion changes needed; short
    /// tables paint fully either way, keeping existing goldens byte-identical.
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click/space path).</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    /// <summary>
    /// Click hit-test for the top-rule row (unified expand gesture, [UX2] #262;
    /// mirrors <c>ToolCallBlock.TryHitHeader</c>).
    /// </summary>
    public bool TryHitHeader(int col, int row)
    {
        if (_lastPaintRect is not { } rect || _lastSkipRows != 0)
        {
            return false;
        }

        return row == rect.Y && col >= rect.X && col < rect.X + rect.Width;
    }

    /// <summary>
    /// Parses the table block at <paramref name="lines" />[<paramref name="index" />]
    /// via <see cref="GfmTableParser" />. Returns false (null block) when the
    /// lines do not open a GFM table.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> lines, int index, out TableBlock? block, out int nextIndex)
    {
        block = null;
        nextIndex = index;
        if (!GfmTableParser.TryParse(lines, index, out var table, out int next))
        {
            return false;
        }

        block = new TableBlock(table);
        nextIndex = next;
        return true;
    }

    public BlockMeasure Measure(int width)
    {
        int total = 4 + _table.Rows.Count;
        if (!IsExpanded && MaxBodyLines > 0 && total > MaxBodyLines)
        {
            // Collapsed overflow ([UX2] #262): top slice + one
            // overflow-marker row + bottom rule (the frame survives the cut).
            return BlockMeasure.Exact(MaxBodyLines + 1);
        }

        return BlockMeasure.Exact(total);
    }

    public int CheapEstimate(int width)
    {
        int total = 4 + _table.Rows.Count;
        if (!IsExpanded && MaxBodyLines > 0 && total > MaxBodyLines)
        {
            return MaxBodyLines + 1;
        }

        return total;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        var buffer = ctx.Buffer;
        int width = ctx.Rect.Width;
        int height = ctx.Rect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureLayout(width);
        int[] widths = _layoutWidths!;
        string[] rules = _layoutRules!;
        var border = ChatPalette.Dim;
        var headerStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);

        int total = 4 + _table.Rows.Count;
        bool overflow = !IsExpanded && MaxBodyLines > 0 && total > MaxBodyLines;
        int hidden = 0;
        if (overflow)
        {
            // Sliced rows 0..MaxBodyLines-2 stay; rows 3.. are body rows.
            int slicedBody = Math.Max(0, (MaxBodyLines - 1) - 3);
            hidden = Math.Max(0, _table.Rows.Count - slicedBody);
        }

        int visible = overflow ? MaxBodyLines + 1 : total;
        int start = Math.Min(ctx.SkipRows, visible);
        int count = Math.Min(height, visible - start);

        for (int i = 0; i < count; i++)
        {
            int row = start + i;
            int y = ctx.Rect.Y + i;
            if (overflow && row == MaxBodyLines - 1)
            {
                // Collapsed overflow tail ([UX2] #262): dim "... (N hidden)"
                // between the last shown body row and the bottom rule.
                string tail = ICollapsibleChatBlock.OverflowTail(hidden);
                buffer.SetText(ctx.Rect.X, y, tail.AsSpan(0, Math.Min(width, tail.Length)), ChatPalette.Dim);
                continue;
            }

            if (overflow && row == MaxBodyLines)
            {
                // Collapsed frame ([UX2] #262): bottom rule from the cached
                // layout (ENG10 one-shot render — no per-frame rebuild).
                buffer.SetText(ctx.Rect.X, y, rules[2], border);
                continue;
            }

            switch (row)
            {
                case 0:
                    buffer.SetText(ctx.Rect.X, y, rules[0], border);
                    break;
                case 1:
                    PaintDataRow(buffer, ctx.Rect.X, y, _layoutCells!, 0, widths, width, headerStyle);
                    break;
                case 2:
                    buffer.SetText(ctx.Rect.X, y, rules[1], border);
                    break;
                case int r when r == total - 1:
                    buffer.SetText(ctx.Rect.X, y, rules[2], border);
                    break;
                default:
                    int bodyRow = row - 3;
                    PaintDataRow(buffer, ctx.Rect.X, y, _layoutCells!, (1 + bodyRow) * Columns, widths, width, CellStyle.Plain);
                    break;
            }
        }
    }

    /// <summary>Rebuilds the cached layout when the paint width or row count changed.</summary>
    private void EnsureLayout(int maxWidth)
    {
        if (_layoutWidths is not null && _layoutWidth == maxWidth && _layoutRows == _table.Rows.Count)
        {
            return;
        }

        var widths = ColumnWidths(maxWidth);
        var rules = new string[3];
        rules[0] = BuildRule(Tl, Tr, X, widths, maxWidth);
        rules[1] = BuildRule(Lt, Rt, X, widths, maxWidth);
        rules[2] = BuildRule(Bl, Br, X, widths, maxWidth);

        int cols = Columns;
        var cells = new string[(1 + _table.Rows.Count) * cols];
        for (int c = 0; c < cols; c++)
        {
            cells[c] = FitCell(_table.Headers[c], widths[c], AlignAt(c));
        }

        for (int r = 0; r < _table.Rows.Count; r++)
        {
            var row = _table.Rows[r];
            for (int c = 0; c < cols; c++)
            {
                string text = c < row.Count ? row[c] : string.Empty;
                cells[(1 + r) * cols + c] = FitCell(text, widths[c], AlignAt(c));
            }
        }

        _layoutWidths = widths;
        _layoutRules = rules;
        _layoutCells = cells;
        _layoutWidth = maxWidth;
        _layoutRows = _table.Rows.Count;
    }

    private GfmAlign AlignAt(int c) => c < _table.Alignments.Count ? _table.Alignments[c] : GfmAlign.Left;

    /// <summary>Copy-friendly pipe form (header + separator + body rows).</summary>
    public string RawText()
    {
        var sb = new StringBuilder();
        AppendPipeRow(sb, _table.Headers);
        sb.Append('\n');
        sb.Append('|');
        for (int c = 0; c < Columns; c++)
        {
            sb.Append(_table.Alignments[c] switch
            {
                GfmAlign.Center => " :---: |",
                GfmAlign.Right => " ---: |",
                _ => " --- |",
            });
        }

        sb.Append('\n');
        foreach (var row in _table.Rows)
        {
            AppendPipeRow(sb, row);
            sb.Append('\n');
        }

        if (sb.Length > 0)
        {
            sb.Length--; // trailing newline
        }

        return sb.ToString();
    }

    private int[] ColumnWidths(int maxWidth)
    {
        int cols = Columns;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int w = Math.Max(MinCell, Math.Min(CellWidth(_table.Headers[c]), MaxCell));
            foreach (var row in _table.Rows)
            {
                string cell = c < row.Count ? row[c] : string.Empty;
                w = Math.Max(w, Math.Max(MinCell, Math.Min(CellWidth(cell), MaxCell)));
            }

            widths[c] = w;
        }

        if (maxWidth > 4)
        {
            int decor = cols + 1 + (2 * cols);
            ShrinkToFit(widths, maxWidth - decor);
        }

        return widths;
    }

    private static void ShrinkToFit(int[] widths, int budget)
    {
        int total = 0;
        for (int c = 0; c < widths.Length; c++)
        {
            total += widths[c];
        }

        while (total > budget)
        {
            int idx = -1;
            for (int c = 0; c < widths.Length; c++)
            {
                if (widths[c] > MinCell && (idx < 0 || widths[c] > widths[idx]))
                {
                    idx = c;
                }
            }

            if (idx < 0)
            {
                break;
            }

            int cut = Math.Min(widths[idx] - MinCell, Math.Max(1, total - budget));
            widths[idx] -= cut;
            total -= cut;
        }
    }

    /// <summary>Rule string builder (cache-fill only — Paint slices the cached rules).</summary>
    private static string BuildRule(
        char left, char right, char mid, int[] widths, int maxWidth)
    {
        var sb = new StringBuilder();
        sb.Append(left);
        for (int c = 0; c < widths.Length; c++)
        {
            sb.Append(H, widths[c] + (2 * Pad));
            sb.Append(c < widths.Length - 1 ? mid : right);
        }

        string rule = sb.ToString();
        int ruleWidth = UnicodeWidth.Width(rule);
        if (ruleWidth > maxWidth)
        {
            rule = HardTruncateCells(rule, maxWidth);
        }

        return rule;
    }

    private static void PaintDataRow(
        ScreenBuffer buffer, int x, int y,
        string[] fitted, int offset, int[] widths,
        int maxWidth, CellStyle cellStyle)
    {
        var border = ChatPalette.Dim;
        int cursor = x;
        int end = x + maxWidth;

        buffer.SetText(cursor, y, [V], border);
        cursor++;
        for (int c = 0; c < widths.Length && cursor < end; c++)
        {
            buffer.SetText(cursor, y, " ", border);
            cursor++;
            if (cursor >= end)
            {
                break;
            }

            // Cell text and padding share one run; borders stay dim.
            buffer.SetText(cursor, y, fitted[offset + c], cellStyle);
            cursor += UnicodeWidth.Width(fitted[offset + c]);
            if (cursor >= end)
            {
                break;
            }

            buffer.SetText(cursor, y, " ", border);
            cursor++;
            if (cursor >= end)
            {
                break;
            }

            buffer.SetText(cursor, y, [V], border);
            cursor++;
        }
    }

    private static string FitCell(string text, int width, GfmAlign align)
    {
        if (UnicodeWidth.Width(text) > width)
        {
            text = HardTruncateCells(text, width);
        }

        int pad = width - UnicodeWidth.Width(text);
        if (pad <= 0)
        {
            return text;
        }

        return align switch
        {
            GfmAlign.Right => new string(' ', pad) + text,
            GfmAlign.Center => new string(' ', pad / 2) + text + new string(' ', pad - (pad / 2)),
            _ => text + new string(' ', pad),
        };
    }

    private static int CellWidth(string cell) => UnicodeWidth.Width(cell);

    private static string HardTruncateCells(string text, int width)
    {
        if (width <= 1)
        {
            return width <= 0 ? string.Empty : "…";
        }

        var sb = new StringBuilder();
        int w = 0;
        var span = text.AsSpan();
        while (!span.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf16(span, out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int cw = Math.Max(1, UnicodeWidth.Width(rune));
            if (w + cw > width - 1)
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

    private static void AppendPipeRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        sb.Append('|');
        foreach (string cell in cells)
        {
            sb.Append(' ').Append(cell).Append(" |");
        }
    }

    private static int CellsBytes(IReadOnlyList<string> cells)
    {
        int bytes = 16;
        foreach (string cell in cells)
        {
            bytes += 16 + (cell.Length * 2);
        }

        return bytes;
    }
}
