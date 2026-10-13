namespace Harbor.Terminal.Abstractions.Rendering;

/// <summary>
/// Span-aware column measurement for GFM tables ([compose/spectre] #1212):
/// desired widths with colspan split, greedy tallest-first collapse with
/// wrappable-first passes and NoWrap last-resort, and symmetric
/// expand-distribute. Follows the Spectre.Console <c>TableMeasurer</c>
/// discipline (MIT): <c>GetNonColumnWidth</c> decor, collapse via
/// <c>Ratio.Reduce</c> analog, expand via <c>Ratio.Distribute</c> analog.
/// Framework-free (pure int math + display width): lives next to the
/// <see cref="GfmTable" /> model so the cell-native <c>TableBlock</c> and the
/// string-grid <c>GfmTableFormatter</c> resolve widths from one place.
/// </summary>
public static class TableMeasurer
{
    /// <summary>Collapse/expand floor: a column never shrinks below this.</summary>
    public const int MinCell = 3;

    /// <summary>
    /// Desired-width cap: one runaway cell (e.g. 100k chars) cannot blow up
    /// measurement; visible truncation to the paint width happens later anyway.
    /// Also the expand ceiling.
    /// </summary>
    public const int MaxCell = 256;

    /// <summary>Single space on each side of a cell (<c>┃ cell ┃</c>).</summary>
    public const int CellPad = 1;

    /// <summary>
    /// One cell's horizontal span over table columns. <see cref="Row" /> is -1
    /// for a header cell, otherwise the 0-based body-row index;
    /// <see cref="Column" /> is the 0-based start column and
    /// <see cref="ColumnSpan" /> the covered column count (≥ 1; 1 = plain cell).
    /// The spanned text is read from the table itself, so a span only names a
    /// position — no model change to <see cref="GfmTable" />.
    /// </summary>
    public readonly record struct TableCellSpan(int Row, int Column, int ColumnSpan);

    /// <summary>
    /// Width consumed by everything that is not a column: one border per
    /// column edge plus <see cref="CellPad" /> on each side of every cell.
    /// Spectre <c>GetNonColumnWidth</c> analog.
    /// </summary>
    public static int GetNonColumnWidth(int columns) => columns + 1 + (2 * CellPad * columns);

    /// <summary>
    /// Per-column desired widths: max display width over the header and every
    /// body row, floored at <see cref="MinCell" /> and capped at
    /// <see cref="MaxCell" />. Span-split: a cell covered by a span from
    /// <paramref name="spans" /> does not widen its own column; instead the
    /// span owner's desired width is split evenly (rounded up) across the
    /// covered columns. Out-of-range spans are ignored.
    /// </summary>
    public static int[] MeasureDesiredWidths(GfmTable table, IReadOnlyList<TableCellSpan>? spans = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        int cols = table.Headers.Count;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            widths[c] = MinCell;
        }

        bool[] covered = spans is null ? [] : CoveredCells(table, spans, header: false);
        bool[] headerCovered = spans is null ? [] : CoveredCells(table, spans, header: true);

        for (int c = 0; c < cols; c++)
        {
            if (!IsCovered(headerCovered, 0, cols, c))
            {
                widths[c] = Math.Max(widths[c], ClampCell(WidthOf(table.Headers[c])));
            }
        }

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            for (int c = 0; c < cols; c++)
            {
                if (IsCovered(covered, r, cols, c))
                {
                    continue;
                }

                string text = c < row.Count ? row[c] : string.Empty;
                widths[c] = Math.Max(widths[c], ClampCell(WidthOf(text)));
            }
        }

        if (spans is not null)
        {
            foreach (var span in spans)
            {
                ApplySpan(table, widths, span);
            }
        }

        return widths;
    }

    /// <summary>
    /// Greedy tallest-first collapse (Spectre <c>Ratio.Reduce</c> analog):
    /// while over <paramref name="budget" />, cut the whole excess from the
    /// widest column still above <see cref="MinCell" />. Columns flagged in
    /// <paramref name="noWrap" /> (single-token columns that must not shrink)
    /// collapse last: only when every wrappable column is pinned at the floor.
    /// When everything is pinned the widths stay — the caller hard-truncates.
    /// </summary>
    public static void CollapseWidths(int[] widths, int budget, bool[]? noWrap = null)
    {
        ArgumentNullException.ThrowIfNull(widths);
        int total = Sum(widths);
        while (total > budget)
        {
            int idx = WidestAboveFloor(widths, noWrap, skipNoWrap: true);
            if (idx < 0)
            {
                idx = WidestAboveFloor(widths, noWrap, skipNoWrap: false);
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

    /// <summary>
    /// Symmetric expand (Spectre <c>Ratio.Distribute</c> analog): while under
    /// <paramref name="budget" />, grow the narrowest column still below
    /// <see cref="MaxCell" /> by the whole remaining surplus. No-op when the
    /// widths already meet or exceed the budget.
    /// </summary>
    public static void DistributeWidths(int[] widths, int budget)
    {
        ArgumentNullException.ThrowIfNull(widths);
        int total = Sum(widths);
        while (total < budget)
        {
            int idx = -1;
            for (int c = 0; c < widths.Length; c++)
            {
                if (widths[c] < MaxCell && (idx < 0 || widths[c] < widths[idx]))
                {
                    idx = c;
                }
            }

            if (idx < 0)
            {
                break;
            }

            int give = Math.Min(MaxCell - widths[idx], Math.Max(1, budget - total));
            widths[idx] += give;
            total += give;
        }
    }

    /// <summary>
    /// Full pipeline: desired widths via <see cref="MeasureDesiredWidths" />,
    /// then <see cref="CollapseWidths" /> when over budget or
    /// <see cref="DistributeWidths" /> when under it and
    /// <paramref name="expandToFill" /> is set. <paramref name="maxWidth" />
    /// is the full paint width including borders and padding; values ≤ 4 skip
    /// fitting (degenerate rect — paint path truncates anyway).
    /// </summary>
    public static int[] CalculateColumnWidths(
        GfmTable table,
        int maxWidth,
        bool expandToFill = false,
        IReadOnlyList<TableCellSpan>? spans = null,
        bool[]? noWrap = null)
    {
        int[] widths = MeasureDesiredWidths(table, spans);
        int cols = widths.Length;
        if (maxWidth > 4 && cols > 0)
        {
            int budget = maxWidth - GetNonColumnWidth(cols);
            if (Sum(widths) > budget)
            {
                CollapseWidths(widths, budget, noWrap);
            }
            else if (expandToFill)
            {
                DistributeWidths(widths, budget);
            }
        }

        return widths;
    }

    /// <summary>
    /// NoWrap inference: a column is non-wrappable when no cell in it contains
    /// whitespace (ids, numbers, single tokens) — collapsing it can only clip
    /// content, never reflow, so it collapses last. Length always equals the
    /// header column count.
    /// </summary>
    public static bool[] InferNoWrap(GfmTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        int cols = table.Headers.Count;
        var noWrap = new bool[cols];
        for (int c = 0; c < cols; c++)
        {
            bool wrap = ContainsWhitespace(table.Headers[c]);
            for (int r = 0; r < table.Rows.Count && !wrap; r++)
            {
                var row = table.Rows[r];
                wrap = c < row.Count && ContainsWhitespace(row[c]);
            }

            noWrap[c] = !wrap;
        }

        return noWrap;
    }

    private static bool[] CoveredCells(GfmTable table, IReadOnlyList<TableCellSpan> spans, bool header)
    {
        int cols = table.Headers.Count;
        int rows = header ? 1 : table.Rows.Count;
        var covered = new bool[rows * cols];
        foreach (var span in spans)
        {
            bool isHeader = span.Row < 0;
            if (isHeader != header)
            {
                continue;
            }

            int row = isHeader ? 0 : span.Row;
            if (!isHeader && (row < 0 || row >= table.Rows.Count))
            {
                continue;
            }

            // The owner cell is covered too: its width is distributed across
            // the span by ApplySpan instead of widening its own column.
            for (int c = span.Column; c < span.Column + span.ColumnSpan && c < cols; c++)
            {
                if (c >= 0)
                {
                    covered[row * cols + c] = true;
                }
            }
        }

        return covered;
    }

    private static bool IsCovered(bool[] covered, int row, int cols, int col) =>
        covered.Length != 0 && col >= 0 && col < cols && covered[row * cols + col];

    private static void ApplySpan(GfmTable table, int[] widths, TableCellSpan span)
    {
        int cols = widths.Length;
        int n = Math.Min(span.ColumnSpan, cols - span.Column);
        if (span.Column < 0 || span.Column >= cols || n <= 1)
        {
            return;
        }

        string text = span.Row < 0
            ? table.Headers[span.Column]
            : span.Row < table.Rows.Count && span.Column < table.Rows[span.Row].Count
                ? table.Rows[span.Row][span.Column]
                : string.Empty;
        int share = (ClampCell(WidthOf(text)) + n - 1) / n;
        for (int c = span.Column; c < span.Column + n; c++)
        {
            widths[c] = Math.Max(widths[c], share);
        }
    }

    private static int WidestAboveFloor(int[] widths, bool[]? noWrap, bool skipNoWrap)
    {
        int idx = -1;
        for (int c = 0; c < widths.Length; c++)
        {
            if (widths[c] <= MinCell)
            {
                continue;
            }

            if (skipNoWrap && noWrap is not null && c < noWrap.Length && noWrap[c])
            {
                continue;
            }

            if (idx < 0 || widths[c] > widths[idx])
            {
                idx = c;
            }
        }

        return idx;
    }

    private static int Sum(int[] widths)
    {
        int total = 0;
        for (int c = 0; c < widths.Length; c++)
        {
            total += widths[c];
        }

        return total;
    }

    private static int ClampCell(int w) => Math.Max(MinCell, Math.Min(w, MaxCell));

    private static bool ContainsWhitespace(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static int WidthOf(string text)
    {
        int w = 0;
        for (int i = 0; i < text.Length; i++)
        {
            w += IsWide(text[i]) ? 2 : 1;
        }

        return w;
    }

    private static bool IsWide(char c) =>
        c is >= '\u1100' and <= '\u115F' or // Hangul Jamo
            >= '\u2E80' and <= '\u303E' or
            >= '\u3041' and <= '\u33FF' or
            >= '\u3400' and <= '\u4DBF' or
            >= '\u4E00' and <= '\u9FFF' or
            >= '\uA000' and <= '\uA4CF' or
            >= '\uAC00' and <= '\uD7A3' or
            >= '\uF900' and <= '\uFAFF' or
            >= '\uFE30' and <= '\uFE4F' or
            >= '\uFF00' and <= '\uFF60' or
            >= '\uFFE0' and <= '\uFFE6';
}
