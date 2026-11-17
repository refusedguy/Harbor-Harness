using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Sort direction applied to a <see cref="SortableTable"/> column.</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>The active sort: a column index and its direction.</summary>
public readonly record struct SortOrder(int Column, SortDirection Direction);

/// <summary>How a column renders its cells.</summary>
public enum TableColumnKind
{
    /// <summary>Cell <see cref="TableCell.Text"/> is painted as plain text and sorted ordinal.</summary>
    Text,

    /// <summary>Cell <see cref="TableCell.Ratio"/> is painted as a <see cref="GaugeBar"/> and sorted numeric.</summary>
    Gauge,
}

/// <summary>Column definition for <see cref="SortableTable"/>.</summary>
public readonly record struct TableColumn(
    string Header,
    TableColumnKind Kind = TableColumnKind.Text,
    GenericColumnAlign Align = GenericColumnAlign.Left);

/// <summary>
/// One table cell. <see cref="Text"/> is the display label; <see cref="Ratio"/>
/// is an optional 0..1 value used by <see cref="TableColumnKind.Gauge"/> columns
/// (painted through <see cref="GaugeBar"/>) and as the numeric sort key.
/// </summary>
public readonly record struct TableCell(string Text, double? Ratio = null)
{
    /// <summary>Plain text cell.</summary>
    public static TableCell Of(string text) => new(text ?? string.Empty);

    /// <summary>Gauge cell: a clamped ratio with an optional centered label.</summary>
    public static TableCell Gauge(double ratio, string? label = null) =>
        new(label ?? string.Empty, GaugeBar.ClampRatio(ratio));
}

/// <summary>
/// General-purpose sortable table primitive (issue #2327, from the k9s/htop/
/// ctop/bottom research). Three properties make it reusable across processes,
/// sessions, logs and files without any domain knowledge:
/// <list type="bullet">
///   <item><description>sort by any column, ascending or descending, with a stable tie-break on source order;</description></item>
///   <item><description>a sticky header that is always painted first, independent of the body scroll offset;</description></item>
///   <item><description>row virtualization: only the visible slice of rows is painted, so a thousand-row table costs the same as a ten-row one.</description></item>
/// </list>
/// Layout and frame painting are delegated to <see cref="GenericTable"/> — the
/// box-drawing, column fitting and sticky-header mechanics stay in one place —
/// while this type owns sorting and the scroll window. Gauge columns are painted
/// with <see cref="GaugeBar"/> over the fitted cell. <see cref="SetRowsFromLines"/>
/// bridges from the plain display lines the <c>PanelRows.*Rows</c> builders
/// produce, sharing their <see cref="PanelText.SingleLine"/> normalizer.
/// Scroll position is passed to <see cref="Paint(ScreenBuffer, Rect, int, int)"/>
/// or supplied by a <see cref="GenericNavState"/>, matching the stateful-widget
/// pattern used across the generic widgets.
/// </summary>
public sealed class SortableTable : IStatefulGenericWidget<GenericNavState>
{
    // GenericTable's frame is top rule + header + mid rule + bottom rule.
    private const int FrameH = 4;

    private readonly List<TableColumn> _columns = new();
    private readonly List<TableCell[]> _rows = new();
    private int[] _order = [];
    private SortOrder? _sort;

    /// <summary>Column definitions in display order.</summary>
    public IReadOnlyList<TableColumn> Columns => _columns;

    /// <summary>Source rows in insertion order (sorting never mutates this).</summary>
    public IReadOnlyList<TableCell[]> Rows => _rows;

    /// <summary>Active sort, or <see langword="null"/> when in source order.</summary>
    public SortOrder? Sort => _sort;

    /// <summary>
    /// Source row indices in display order. Read-only snapshot of the current
    /// sort; index <c>k</c> is the source row painted at display position <c>k</c>.
    /// </summary>
    public IReadOnlyList<int> Order => _order;

    /// <summary>Replaces the column definitions (clears any active sort).</summary>
    public void SetColumns(IReadOnlyList<TableColumn> columns)
    {
        Ensure.NotNull(columns);
        _columns.Clear();
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            _columns.Add(new TableColumn(
                column.Header ?? string.Empty,
                column.Kind,
                column.Align));
        }

        _sort = null;
        RebuildOrder();
    }

    /// <summary>Replaces the rows (keeps the active sort).</summary>
    public void SetRows(IReadOnlyList<TableCell[]> rows)
    {
        Ensure.NotNull(rows);
        _rows.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            _rows.Add(CopyRow(rows[i]));
        }

        RebuildOrder();
    }

    /// <summary>Appends one row (copied; padded/truncated to the column count on paint).</summary>
    public void AddRow(IReadOnlyList<TableCell> cells)
    {
        Ensure.NotNull(cells);
        var row = new TableCell[Math.Max(1, _columns.Count)];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = i < cells.Count ? cells[i] : default;
        }

        _rows.Add(row);
        RebuildOrder();
    }

    /// <summary>
    /// Fills rows from plain display lines, splitting each on
    /// <paramref name="separator"/>. This is the bridge from the PanelRows
    /// family: a panel that already builds display lines via
    /// <c>PanelRows.*Rows</c> can hand them to the table without re-modeling
    /// its data. Every resulting cell is a plain text cell.
    /// </summary>
    public void SetRowsFromLines(IReadOnlyList<string> lines, char separator = '|')
    {
        Ensure.NotNull(lines);
        var rows = new List<TableCell[]>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            // PanelRows log builders can embed CR/LF in a message; keep one
            // display row per line using the shared PanelText normalizer.
            string line = PanelText.SingleLine(lines[i]);
            string[] parts = line.Split(separator);
            var cells = new TableCell[parts.Length];
            for (int c = 0; c < parts.Length; c++)
            {
                cells[c] = TableCell.Of(parts[c]);
            }

            rows.Add(cells);
        }

        SetRows(rows);
    }

    /// <summary>Clears all rows (columns and sort survive).</summary>
    public void ClearRows()
    {
        _rows.Clear();
        RebuildOrder();
    }

    /// <summary>Sorts by <paramref name="column"/> in <paramref name="direction"/>.</summary>
    public void SortBy(int column, SortDirection direction = SortDirection.Ascending)
    {
        if (_columns.Count == 0)
        {
            return;
        }

        int col = Math.Clamp(column, 0, _columns.Count - 1);
        _sort = new SortOrder(col, direction);
        RebuildOrder();
    }

    /// <summary>
    /// Cycles a column ascending → descending → ascending. A different column
    /// starts at ascending.
    /// </summary>
    public void ToggleSort(int column)
    {
        if (_columns.Count == 0)
        {
            return;
        }

        int col = Math.Clamp(column, 0, _columns.Count - 1);
        if (_sort is { } current && current.Column == col)
        {
            _sort = new SortOrder(col, current.Direction == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending);
        }
        else
        {
            _sort = new SortOrder(col, SortDirection.Ascending);
        }

        RebuildOrder();
    }

    /// <summary>Removes the sort, restoring source order.</summary>
    public void ClearSort()
    {
        _sort = null;
        RebuildOrder();
    }

    /// <summary>Body rows that fit under the sticky header in <paramref name="height"/> cells.</summary>
    public int BodyCapacity(int height) => Math.Max(0, height - FrameH);

    /// <summary>
    /// Pure virtualization window over the display order for a given capacity
    /// and scroll offset. Both bounds are clamped, so the result is always a
    /// valid slice of <see cref="Order"/>.
    /// </summary>
    public (int First, int LastExclusive) VisibleRows(int capacity, int offset)
    {
        int rows = _rows.Count;
        if (rows == 0 || capacity <= 0)
        {
            return (0, 0);
        }

        int first = Math.Clamp(offset, 0, Math.Max(0, rows - capacity));
        int last = Math.Min(rows, first + capacity);
        return (first, last);
    }

    /// <inheritdoc />
    public void Paint(ScreenBuffer buffer, Rect rect, GenericNavState state)
    {
        Ensure.NotNull(state);
        state.Sync(BodyCapacity(rect.Height));
        Paint(buffer, rect, state.Offset, state.Selected);
    }

    /// <summary>
    /// Paints the table. The header is always drawn at <paramref name="rect"/>'s
    /// top row (sticky); only body rows scroll. <paramref name="offset"/> indexes
    /// the display order, so sorting and virtualization compose.
    /// </summary>
    /// <param name="buffer">Paint target.</param>
    /// <param name="rect">Bounds.</param>
    /// <param name="offset">First visible body row in display order.</param>
    /// <param name="selectedRow">Display-order row to highlight, or -1 for none.</param>
    public void Paint(ScreenBuffer buffer, Rect rect, int offset, int selectedRow = -1)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _columns.Count == 0)
        {
            return;
        }

        var (first, last) = VisibleRows(BodyCapacity(rect.Height), offset);

        // Build a display-only GenericTable with just the visible slice. Its
        // own frame, column fitting and sticky header do the layout work.
        var frame = new GenericTable();
        frame.SetHeaders(HeaderCells(), ColumnAligns());
        for (int display = first; display < last; display++)
        {
            frame.AddRow(DisplayCells(_rows[_order[display]]));
        }

        frame.SelectedRow = selectedRow >= first && selectedRow < last ? selectedRow - first : -1;
        frame.Paint(buffer, rect);

        if (HasGaugeColumn())
        {
            PaintGauges(buffer, rect, frame, first, last);
        }
    }

    private void PaintGauges(ScreenBuffer buffer, Rect rect, GenericTable frame, int first, int last)
    {
        int[] widths = frame.MeasureWidths(rect.Width);
        for (int c = 0; c < widths.Length && c < _columns.Count; c++)
        {
            if (_columns[c].Kind != TableColumnKind.Gauge)
            {
                continue;
            }

            int contentX = rect.X + 2;
            for (int k = 0; k < c; k++)
            {
                contentX += widths[k] + 3; // content + trailing space + border + leading space
            }

            for (int display = first; display < last; display++)
            {
                int y = rect.Y + 3 + (display - first);
                if (y >= rect.Y + rect.Height - 1)
                {
                    continue;
                }

                var cell = Cell(_rows[_order[display]], c);
                GaugeBar.Paint(
                    buffer,
                    new Rect(contentX, y, widths[c], 1),
                    new GaugeState(cell.Ratio ?? 0, cell.Text));
            }
        }
    }

    private void RebuildOrder()
    {
        int n = _rows.Count;
        _order = new int[n];
        for (int i = 0; i < n; i++)
        {
            _order[i] = i;
        }

        if (_sort is not { } sort || n < 2 || sort.Column >= _columns.Count)
        {
            return;
        }

        int column = sort.Column;
        bool desc = sort.Direction == SortDirection.Descending;
        var kind = _columns[column].Kind;
        Array.Sort(_order, (a, b) =>
        {
            int cmp = CompareCells(a, b, column, kind);
            if (cmp == 0)
            {
                return a.CompareTo(b);
            }

            return desc ? -cmp : cmp;
        });
    }

    private int CompareCells(int a, int b, int column, TableColumnKind kind)
    {
        var ca = Cell(_rows[a], column);
        var cb = Cell(_rows[b], column);
        if (kind == TableColumnKind.Gauge)
        {
            return (ca.Ratio ?? 0).CompareTo(cb.Ratio ?? 0);
        }

        return string.CompareOrdinal(ca.Text, cb.Text);
    }

    private string[] HeaderCells()
    {
        var cells = new string[_columns.Count];
        for (int c = 0; c < cells.Length; c++)
        {
            cells[c] = HeaderText(c);
        }

        return cells;
    }

    private string HeaderText(int column)
    {
        string header = _columns[column].Header;
        if (_sort is not { } sort || sort.Column != column)
        {
            return header;
        }

        return header + (sort.Direction == SortDirection.Ascending ? " ▲" : " ▼");
    }

    private GenericColumnAlign[] ColumnAligns()
    {
        var aligns = new GenericColumnAlign[_columns.Count];
        for (int c = 0; c < aligns.Length; c++)
        {
            aligns[c] = _columns[c].Align;
        }

        return aligns;
    }

    private string[] DisplayCells(TableCell[] row)
    {
        var cells = new string[_columns.Count];
        for (int c = 0; c < cells.Length; c++)
        {
            cells[c] = Cell(row, c).Text ?? string.Empty;
        }

        return cells;
    }

    private bool HasGaugeColumn()
    {
        for (int c = 0; c < _columns.Count; c++)
        {
            if (_columns[c].Kind == TableColumnKind.Gauge)
            {
                return true;
            }
        }

        return false;
    }

    private static TableCell Cell(TableCell[] row, int column) =>
        column >= 0 && column < row.Length ? row[column] : default;

    private static TableCell[] CopyRow(IReadOnlyList<TableCell>? cells)
    {
        int count = cells?.Count ?? 0;
        var row = new TableCell[Math.Max(1, count)];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = cells is not null && i < cells.Count ? cells[i] : default;
        }

        return row;
    }
}
