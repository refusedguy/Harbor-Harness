using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Cell-native auto-column grid over single-line strings (file grids, pills,
/// tags): the Spectre <c>Widgets/Columns.cs</c> port ([compose/spectre] #1213,
/// epic #1195). Column count comes from the borrowed
/// <see cref="CalculateColumnCount"/> fit loop (per-column maxima + gap must
/// fit <c>maxWidth</c>, shrinking the candidate count until they do);
/// painting is cell space (wide-rune aware via <see cref="UnicodeWidth"/>)
/// with per-row <see cref="ScreenBuffer.SetText" /> calls in
/// <see cref="CellStyle.Plain" /> — never segment strings, never cursor
/// escapes. Height is width-dependent but wrap-free: items fill rows
/// left-to-right (row-major, like the source), so
/// <c>rows = ceil(count / columns)</c>. Over-wide cells hard-truncate with
/// <c>…</c>. Deliberately <see cref="IChatBlock"/>-plain (no collapse
/// mixin): a tag grid is fully visible or it is not painted.
/// </summary>
public sealed class ColumnsBlock : IChatBlock
{
    /// <summary>Default inter-column gap in cells (breathing room for pills/tags).</summary>
    public const int DefaultGap = 2;

    private readonly string[] _items;
    private readonly int _budgetBytes;
    private readonly string _gapText;

    // ENG10 one-shot layout cache (same discipline as TableBlock._layout*):
    // items are immutable, so columns / per-column widths rebuild only when
    // the paint width changes — steady-state Paint only slices spans.
    private int[]? _layoutWidths;
    private int _layoutColumns;
    private int _layoutWidth = -1;

    /// <summary>Copies <paramref name="items"/> (null entries coerce to empty).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gap"/> is negative.</exception>
    public ColumnsBlock(IReadOnlyList<string> items, int gap = DefaultGap)
    {
        Ensure.NotNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        _items = new string[items.Count];
        int bytes = 64;
        for (int i = 0; i < items.Count; i++)
        {
            _items[i] = items[i] ?? string.Empty;
            bytes += 16 + (_items[i].Length * 2);
        }

        _budgetBytes = bytes;
        Gap = gap;
        _gapText = new string(' ', gap);
    }

    public string Kind => "columns";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => _budgetBytes;

    /// <summary>Items in order (row-major fill).</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>Inter-column gap in cells.</summary>
    public int Gap { get; }

    /// <summary>Item count (grid cells).</summary>
    public int Count => _items.Length;

    public BlockMeasure Measure(int width)
    {
        if (_items.Length == 0)
        {
            return BlockMeasure.Exact(0);
        }

        return BlockMeasure.Exact(RowCount(width));
    }

    public int CheapEstimate(int width)
    {
        // Single-column upper bound: pure arithmetic, no measurement.
        return _items.Length;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int width = ctx.Rect.Width;
        int height = ctx.Rect.Height;
        if (width <= 0 || height <= 0 || _items.Length == 0)
        {
            return;
        }

        int columns = EnsureLayout(width);
        int[] colWidths = _layoutWidths!;
        int rows = (_items.Length + columns - 1) / columns;

        int start = Math.Min(ctx.SkipRows, rows);
        int count = Math.Min(height, rows - start);

        for (int i = 0; i < count; i++)
        {
            int row = start + i;
            int y = ctx.Rect.Y + i;
            int x = ctx.Rect.X;
            int end = x + width;

            for (int c = 0; c < columns && x < end; c++)
            {
                int idx = (row * columns) + c;
                if (idx >= _items.Length)
                {
                    break;
                }

                int avail = end - x;
                int fit = Math.Min(colWidths[c], avail);
                string cell = Fit(_items[idx], fit);

                buffer.SetText(x, y, cell, CellStyle.Plain);
                x += UnicodeWidth.Width(cell);

                // Inter-column gap (no trailing gap past the last column).
                if (c < columns - 1 && Gap > 0 && x < end)
                {
                    int gap = Math.Min(Gap, end - x);
                    buffer.SetText(x, y, _gapText.AsSpan(0, gap), CellStyle.Plain);
                    x += gap;
                }
            }
        }
    }

    /// <summary>Copy-friendly plain text: items gap-joined on one row.</summary>
    public string RawText()
    {
        if (_items.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < _items.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(_gapText);
            }

            sb.Append(_items[i]);
        }

        return sb.ToString();
    }

    /// <summary>Painted row count for <paramref name="width"/> (pure, for tests).</summary>
    public int RowCount(int width)
    {
        if (_items.Length == 0)
        {
            return 0;
        }

        int columns = LayoutColumns(width);
        return (_items.Length + columns - 1) / columns;
    }

    /// <summary>
    /// Fitted column count for <paramref name="width"/> (pure, for tests):
    /// the <see cref="CalculateColumnCount"/> loop over measured item widths.
    /// </summary>
    public int LayoutColumns(int width)
    {
        if (_items.Length == 0 || width <= 0)
        {
            return _items.Length == 0 ? 0 : 1;
        }

        var widths = new int[_items.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            widths[i] = UnicodeWidth.Width(_items[i]);
        }

        int columns = CalculateColumnCount(width, widths, _items.Length, Gap);
        return Math.Max(1, columns);
    }

    private int[] ColumnWidths(int columns)
    {
        var widths = new int[columns];
        for (int i = 0; i < _items.Length; i++)
        {
            int c = i % columns;
            int w = UnicodeWidth.Width(_items[i]);
            if (w > widths[c])
            {
                widths[c] = w;
            }
        }

        return widths;
    }

    /// <summary>Rebuilds the cached layout when the paint width changed.</summary>
    private int EnsureLayout(int width)
    {
        if (_layoutWidths is not null && _layoutWidth == width)
        {
            return _layoutColumns;
        }

        int columns = LayoutColumns(width);
        _layoutWidths = ColumnWidths(columns);
        _layoutColumns = columns;
        _layoutWidth = width;
        return columns;
    }

    // Algorithm borrowed from https://github.com/willmcgugan/rich/blob/master/rich/columns.py
    // (via Spectre.Console Widgets/Columns.cs): try the candidate count, track
    // each column's max; when columns + gaps overflow, retry with one fewer
    // column than currently tracked. Pure and width-agnostic — cell widths in,
    // fitted count out.
    internal static int CalculateColumnCount(int maxWidth, IReadOnlyList<int> itemWidths, int columnCount, int padding)
    {
        var widths = new Dictionary<int, int>();
        while (columnCount > 1)
        {
            int columnIndex = 0;
            widths.Clear();

            bool exceededTotalWidth = false;
            foreach (int renderableWidth in IterateWidths(itemWidths, columnCount))
            {
                widths[columnIndex] = Math.Max(widths.GetValueOrDefault(columnIndex, 0), renderableWidth);
                int totalWidth = 0;
                foreach (int w in widths.Values)
                {
                    totalWidth += w;
                }

                totalWidth += padding * (widths.Count - 1);
                if (totalWidth > maxWidth)
                {
                    columnCount = widths.Count - 1;
                    exceededTotalWidth = true;
                    break;
                }
                else
                {
                    columnIndex = (columnIndex + 1) % columnCount;
                }
            }

            if (!exceededTotalWidth)
            {
                break;
            }
        }

        return columnCount;
    }

    private static IEnumerable<int> IterateWidths(IReadOnlyList<int> itemWidths, int columnCount)
    {
        for (int i = 0; i < itemWidths.Count; i++)
        {
            yield return itemWidths[i];
        }

        if (itemWidths.Count % columnCount != 0)
        {
            for (int i = 0; i < columnCount - (itemWidths.Count % columnCount) - 1; i++)
            {
                yield return 0;
            }
        }
    }

    private static string Fit(string text, int width)
    {
        if (UnicodeWidth.Width(text) > width)
        {
            text = Truncate(text, width);
        }

        int pad = width - UnicodeWidth.Width(text);
        if (pad <= 0)
        {
            return text;
        }

        return text + new string(' ', pad);
    }

    private static string Truncate(string text, int width)
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
}
