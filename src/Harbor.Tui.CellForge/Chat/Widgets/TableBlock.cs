using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Terminal.Abstractions.Rendering;
using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// When the selection highlight paints (ratatui
/// <c>highlight_spacing.rs</c> parity, steal #1166).
/// TableBlock paints no gutter column — the highlight is a Reverse run over
/// the selected cells — so this only gates <i>whether</i> the highlight
/// paints, never the layout: defaults keep existing goldens byte-identical.
/// </summary>
public enum TableHighlightSpacing
{
    /// <summary>Paint the highlight whenever a selection is held.</summary>
    Always,

    /// <summary>Paint the highlight only while a row/column/cell is selected (default).</summary>
    WhenSelected,

    /// <summary>Never paint the selection highlight, even when a selection is set.</summary>
    Never,
}

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
///
/// Row selection lives outside the widget (#1158, ratatui
/// <c>StatefulWidget</c> analog, same recipe as <c>GenericTable</c> #1371):
/// <see cref="RowState"/> survives frames and owns the row axis, while this
/// type owns content and geometry. <see cref="Paint(BlockPaintContext,
/// GenericNavState)"/> renders with any count-compatible state, so two blocks
/// can share one. The legacy selection API + <see cref="Paint(BlockPaintContext)"/> path is unchanged (no selection by default, host
/// <c>SkipRows</c> still owns the viewport — no scroll offset here, unlike the
/// generic table). The column axis stays widget-local (out of slice).
/// </summary>
public sealed class TableBlock : ICollapsibleChatBlock, IStatefulGenericWidget<GenericNavState>
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
    private readonly GenericNavState _rowState = new();

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
        _rowState.SetContent(table.Rows.Count);
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

    /// <summary>
    /// Opt-in expand-to-fill (SC4 #1212): when the desired widths fit the paint
    /// width, distribute the surplus across columns via
    /// <see cref="TableMeasurer.DistributeWidths" /> instead of leaving the
    /// table shrink-to-fit. Defaults to <c>false</c> so existing goldens stay
    /// byte-identical; hosts that own the full row width can enable it.
    /// </summary>
    public bool ExpandToFill { get; set; }

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    /// <summary>
    /// Selected body-row index (0-based, header excluded); None = none.
    /// Ratatui <c>table/state.rs</c> parity (steal #1166): the row half of
    /// <c>TableState.selected</c>. A <see cref="Maybe{T}"/> so every consumer
    /// handles "no selection" explicitly (#592: no selection IS widget state).
    /// No selection by default, so existing goldens stay byte-identical.
    /// </summary>
    public Maybe<int> SelectedRow { get; private set; } = Maybe<int>.None;

    /// <summary>
    /// Selected column index (0-based); None = none.
    /// Ratatui <c>TableState.selected_column</c> parity (steal #1166).
    /// </summary>
    public Maybe<int> SelectedColumn { get; private set; } = Maybe<int>.None;

    /// <summary>
    /// Selected cell (row, column) when both axes are set; None otherwise.
    /// Ratatui <c>TableState.selected_cell</c> parity (steal #1166).
    /// </summary>
    public Maybe<(int Row, int Column)> SelectedCell =>
        SelectedRow.HasValue && SelectedColumn.HasValue
            ? Maybe<(int Row, int Column)>.From((SelectedRow.Value, SelectedColumn.Value))
            : Maybe<(int Row, int Column)>.None;

    /// <summary>
    /// Gates whether the selection highlight paints (ratatui
    /// <c>HighlightSpacing</c> parity). Default <see cref="TableHighlightSpacing.WhenSelected"/>.
    /// </summary>
    public TableHighlightSpacing HighlightSpacing { get; set; } = TableHighlightSpacing.WhenSelected;

    /// <summary>Body-row count (header excluded).</summary>
    public int BodyRows => _table.Rows.Count;

    /// <summary>
    /// External row-navigation state (selection on the row axis), count-synced
    /// with <see cref="BodyRows"/> at construction (content is immutable, so no
    /// re-sync is needed later). Mutating it directly (e.g. <see
    /// cref="GenericNavState.SelectNext"/>) drives the <see cref="Paint(BlockPaintContext,
    /// GenericNavState)"/> overload. The legacy <see
    /// cref="Paint(BlockPaintContext)"/> path ignores it, so display-only
    /// hosts stay byte-identical.
    /// </summary>
    public GenericNavState RowState => _rowState;

    /// <summary>
    /// Whether the selection highlight paints right now: <see cref="HighlightSpacing"/>
    /// decides against the current selection (ratatui <c>should_add</c> parity).
    /// </summary>
    public bool ShouldHighlight =>
        ShouldPaintHighlight(HighlightSpacing, SelectedRow.HasValue, SelectedColumn.HasValue);

    private static bool ShouldPaintHighlight(TableHighlightSpacing spacing, bool hasRow, bool hasColumn) =>
        spacing switch
        {
            TableHighlightSpacing.Always => true,
            TableHighlightSpacing.WhenSelected => hasRow || hasColumn,
            _ => false,
        };

    /// <summary>
    /// Selects a body row (clamped to <c>[0, BodyRows)</c>; null clears).
    /// Ratatui <c>TableState.select</c> parity — except the offset reset is
    /// dropped: the host <c>SkipRows</c> owns the viewport, like the scrollbar
    /// steal (#1164) kept <c>ScrollableViewport</c> the authority.
    /// </summary>
    public void SelectRow(int? row) => SelectedRow = ToSelection(ClampRow(row));

    /// <summary>
    /// Selects a column (clamped to <c>[0, Columns)</c>; null clears).
    /// Ratatui <c>TableState.select_column</c> parity (steal #1166).
    /// </summary>
    public void SelectColumn(int? column) => SelectedColumn = ToSelection(ClampColumn(column));

    /// <summary>
    /// Selects a cell (both axes clamped; null clears both).
    /// Ratatui <c>TableState.select_cell</c> parity (steal #1166).
    /// </summary>
    public void SelectCell((int Row, int Column)? cell)
    {
        if (cell is not { } c)
        {
            SelectedRow = Maybe<int>.None;
            SelectedColumn = Maybe<int>.None;
            return;
        }

        SelectedRow = ToSelection(ClampRow(c.Row));
        SelectedColumn = ToSelection(ClampColumn(c.Column));
    }

    /// <summary>Clears the row, column and cell selection.</summary>
    public void ClearSelection()
    {
        SelectedRow = Maybe<int>.None;
        SelectedColumn = Maybe<int>.None;
    }

    /// <summary>
    /// Selects the next body row (None → first; saturating at last, no wrap).
    /// Ratatui <c>select_next</c> parity, clamped eagerly: counts are known
    /// here, so no <c>usize::MAX</c> sentinel survives the call.
    /// </summary>
    public void SelectNext() => SelectedRow = SelectedRow.HasValue
        ? ToSelection(ClampRow(SelectedRow.Value + 1))
        : ToSelection(ClampRow(0));

    /// <summary>
    /// Selects the previous body row (None → last; saturating at first).
    /// Ratatui <c>select_previous</c> parity (steal #1166).
    /// </summary>
    public void SelectPrevious() => SelectedRow = SelectedRow.HasValue
        ? ToSelection(ClampRow(SelectedRow.Value - 1))
        : ToSelection(ClampRow(BodyRows - 1));

    /// <summary>Selects the first body row. Ratatui <c>select_first</c> parity.</summary>
    public void SelectFirst() => SelectedRow = ToSelection(ClampRow(0));

    /// <summary>Selects the last body row. Ratatui <c>select_last</c> parity.</summary>
    public void SelectLast() => SelectedRow = ToSelection(ClampRow(BodyRows - 1));

    /// <summary>
    /// Selects the next column (None → first; saturating at last, no wrap).
    /// Ratatui <c>select_next_column</c> parity (steal #1166).
    /// </summary>
    public void SelectNextColumn() => SelectedColumn = SelectedColumn.HasValue
        ? ToSelection(ClampColumn(SelectedColumn.Value + 1))
        : ToSelection(ClampColumn(0));

    /// <summary>
    /// Selects the previous column (None → last; saturating at first).
    /// Ratatui <c>select_previous_column</c> parity (steal #1166).
    /// </summary>
    public void SelectPreviousColumn() => SelectedColumn = SelectedColumn.HasValue
        ? ToSelection(ClampColumn(SelectedColumn.Value - 1))
        : ToSelection(ClampColumn(Columns - 1));

    /// <summary>Selects the first column. Ratatui <c>select_first_column</c> parity.</summary>
    public void SelectFirstColumn() => SelectedColumn = ToSelection(ClampColumn(0));

    /// <summary>Selects the last column. Ratatui <c>select_last_column</c> parity.</summary>
    public void SelectLastColumn() => SelectedColumn = ToSelection(ClampColumn(Columns - 1));

    /// <summary>
    /// Moves the row selection down by <paramref name="amount"/> (saturating).
    /// Ratatui <c>scroll_down_by</c> parity (steal #1166).
    /// </summary>
    public void ScrollDownBy(int amount) => SelectedRow =
        ToSelection(ClampRow((SelectedRow.HasValue ? SelectedRow.Value : 0) + Math.Max(0, amount)));

    /// <summary>
    /// Moves the row selection up by <paramref name="amount"/> (saturating).
    /// Ratatui <c>scroll_up_by</c> parity (steal #1166).
    /// </summary>
    public void ScrollUpBy(int amount) => SelectedRow =
        ToSelection(ClampRow((SelectedRow.HasValue ? SelectedRow.Value : 0) - Math.Max(0, amount)));

    /// <summary>
    /// Moves the column selection right by <paramref name="amount"/> (saturating).
    /// Ratatui <c>scroll_right_by</c> parity (steal #1166).
    /// </summary>
    public void ScrollRightBy(int amount) => SelectedColumn =
        ToSelection(ClampColumn((SelectedColumn.HasValue ? SelectedColumn.Value : 0) + Math.Max(0, amount)));

    /// <summary>
    /// Moves the column selection left by <paramref name="amount"/> (saturating).
    /// Ratatui <c>scroll_left_by</c> parity (steal #1166).
    /// </summary>
    public void ScrollLeftBy(int amount) => SelectedColumn =
        ToSelection(ClampColumn((SelectedColumn.HasValue ? SelectedColumn.Value : 0) - Math.Max(0, amount)));

    private static Maybe<int> ToSelection(int? index) =>
        index.HasValue ? Maybe<int>.From(index.Value) : Maybe<int>.None;

    private int? ClampRow(int? row)
    {
        if (row is not { } r || BodyRows == 0)
        {
            return null;
        }

        return Math.Clamp(r, 0, BodyRows - 1);
    }

    private int? ClampColumn(int? column)
    {
        if (column is not { } c)
        {
            return null;
        }

        return Math.Clamp(c, 0, Columns - 1);
    }

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

    /// <summary>
    /// Paints the frame, header and body rows into <paramref name="ctx"/>.
    /// Legacy path: highlights the widget-local <see cref="SelectedRow"/> /
    /// <see cref="SelectedColumn"/>; the viewport stays host-owned via
    /// <c>SkipRows</c>.
    /// </summary>
    public void Paint(in BlockPaintContext ctx) => PaintCore(ctx, SelectedRow);

    /// <summary>
    /// Paints with an external row-navigation state (#1158): when <paramref
    /// name="rowState"/> is count-compatible (<c>Count == BodyRows</c>) the row
    /// highlight comes from <c>rowState.Selected</c> (-1 = none), so one state
    /// object can drive two blocks; otherwise falls back to the widget-local
    /// <see cref="SelectedRow"/>. The column axis stays widget-local. No
    /// scrolling: the host <c>SkipRows</c> still owns the viewport.
    /// </summary>
    public void Paint(in BlockPaintContext ctx, GenericNavState rowState)
    {
        ArgumentNullException.ThrowIfNull(rowState);
        // NOTE: named rowSel (not row) — CellForgeEngineAbsenceAxisTests collects
        // Maybe-typed names per file, so a Maybe row local here would collide with
        // ClampRow's nullable-int pattern below and trip the struct-unaware gate.
        Maybe<int> rowSel = rowState.Count == BodyRows && rowState.Selected >= 0
            ? Maybe<int>.From(rowState.Selected)
            : SelectedRow;
        PaintCore(ctx, rowSel);
    }

    /// <inheritdoc />
    public void Paint(ScreenBuffer buffer, Rect rect, GenericNavState rowState) =>
        Paint(new BlockPaintContext(buffer, rect, 0), rowState);

    private void PaintCore(in BlockPaintContext ctx, Maybe<int> rowSelection)
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
        var selectedStyle = new CellStyle(ChatPalette.Text, attrs: StyleAttr.Reverse);
        bool highlight = ShouldPaintHighlight(HighlightSpacing, rowSelection.HasValue, SelectedColumn.HasValue);

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
                    PaintDataRow(buffer, ctx.Rect.X, y, _layoutCells!, 0, widths, width, headerStyle,
                        -1, rowSelection, SelectedColumn, selectedStyle, highlight);
                    break;
                case 2:
                    buffer.SetText(ctx.Rect.X, y, rules[1], border);
                    break;
                case int r when r == total - 1:
                    buffer.SetText(ctx.Rect.X, y, rules[2], border);
                    break;
                default:
                    int bodyRow = row - 3;
                    PaintDataRow(buffer, ctx.Rect.X, y, _layoutCells!, (1 + bodyRow) * Columns, widths, width, CellStyle.Plain,
                        bodyRow, rowSelection, SelectedColumn, selectedStyle, highlight);
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
        // SC4 #1212: desired widths measured here (cell-space UnicodeWidth),
        // collapse/distribute resolved by the shared TableMeasurer — same
        // greedy tallest-first order as the inlined ShrinkToFit before it,
        // so default output is byte-identical; ExpandToFill opts into
        // DistributeWidths instead of shrink-to-fit.
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
            int budget = maxWidth - TableMeasurer.GetNonColumnWidth(cols);
            int total = 0;
            for (int c = 0; c < cols; c++)
            {
                total += widths[c];
            }

            if (total > budget)
            {
                TableMeasurer.CollapseWidths(widths, budget);
            }
            else if (ExpandToFill)
            {
                TableMeasurer.DistributeWidths(widths, budget);
            }
        }

        return widths;
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
        int maxWidth, CellStyle cellStyle,
        int rowIndex, Maybe<int> selectedRow, Maybe<int> selectedColumn,
        CellStyle selectedStyle, bool highlight)
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
            // Steal #1166: ratatui row/column/cell highlight — row-only
            // selects the whole body row, column-only the whole column
            // (header cell included), both a single cell. No selection (or
            // HighlightSpacing.Never) keeps the old paint byte-identical.
            bool selected = highlight && IsSelectedCell(rowIndex, c, selectedRow, selectedColumn);
            buffer.SetText(cursor, y, fitted[offset + c], selected ? selectedStyle : cellStyle);
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

    /// <summary>
    /// Selection test for one painted cell (<paramref name="rowIndex"/> -1 =
    /// header). Row-only selects the whole body row, column-only the whole
    /// column including its header cell, both a single cell.
    /// </summary>
    private static bool IsSelectedCell(int rowIndex, int column, Maybe<int> selectedRow, Maybe<int> selectedColumn)
    {
        if (rowIndex < 0)
        {
            return selectedColumn.HasValue && selectedColumn.Value == column;
        }

        if (selectedRow.HasValue && selectedColumn.HasValue)
        {
            return rowIndex == selectedRow.Value && column == selectedColumn.Value;
        }

        if (selectedRow.HasValue)
        {
            return rowIndex == selectedRow.Value;
        }

        return selectedColumn.HasValue && selectedColumn.Value == column;
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
