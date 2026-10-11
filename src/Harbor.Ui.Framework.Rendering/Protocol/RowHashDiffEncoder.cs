namespace Harbor.Ui.Framework.Rendering.Protocol;

using System.Collections.Immutable;

/// <summary>
///     Portable cell-diff encoder over the shared <see cref="ScreenBuffer"/>
///     (renderer-unification sprint Phase 6.2). Implements the same
///     accelerations as CellForge's DiffEngine — row-hash fast path plus
///     FrameHint damage rects with the same 25 % fallback threshold — while
///     emitting portable <see cref="CellDiffBatch"/>es instead of ANSI, so any
///     backend can adopt differential rendering without touching CellForge
///     internals (hard rule: CellForge optimizations stay untouched behind
///     adapters).
/// </summary>
/// <remarks>
///     Steady-state allocation behavior: the changed-cell staging array is
///     retained across <see cref="Encode"/> calls and only grows (amortized),
///     so repeated frames with a bounded change count do not allocate beyond
///     the returned batch's immutable backing store.
/// </remarks>
public sealed class RowHashDiffEncoder : ICellDiffEncoder
{
    /// <summary>Hints above this share of the screen fall back to full scan (canonical value: <see cref="CellDiffHints.HintAreaThreshold"/>).</summary>
    public const double HintAreaThreshold = CellDiffHints.HintAreaThreshold;

    private CellDiffMessage[] _staging = [];

    /// <inheritdoc />
    public CellDiffBatch Encode(
        ScreenBuffer prev,
        ScreenBuffer next,
        IReadOnlyList<Rect>? hints,
        long sequence)
    {
        if (prev.Cols != next.Cols || prev.Rows != next.Rows)
        {
            throw new ArgumentException(
                $"ScreenBuffer dimensions differ: prev {prev.Cols}x{prev.Rows}, next {next.Cols}x{next.Rows}.");
        }

        int cols = next.Cols;
        int rows = next.Rows;
        bool useHints = hints is { Count: > 0 } && HintAreaWithinThreshold(hints, cols, rows);

        // Full scan when no usable hints; otherwise only the rows the hinted
        // damage rects touch (clamped to the frame).
        int rowStart = 0;
        int rowEnd = rows;
        if (useHints)
        {
            rowStart = rows;
            rowEnd = 0;
            for (int i = 0; i < hints!.Count; i++)
            {
                Rect r = hints[i];
                int top = Math.Clamp(r.Y, 0, rows);
                int bottom = Math.Clamp(r.Y + r.Height, 0, rows);
                if (top < rowStart)
                {
                    rowStart = top;
                }

                if (bottom > rowEnd)
                {
                    rowEnd = bottom;
                }
            }
        }

        int count = 0;
        for (int y = rowStart; y < rowEnd; y++)
        {
            // Row-hash fast path: an equal hash means the row is unchanged —
            // the same invariant DiffEngine's fuzz tests rely on. Hashes are
            // computed lazily and cached inside each buffer. The hash folds
            // the directive table, so an option-only change still breaks
            // equality here (R1 steal).
            if (!useHints && prev.RowHashCode(y) == next.RowHashCode(y))
            {
                continue;
            }

            for (int x = 0; x < cols;)
            {
                var nopt = next.GetDiffOption(x, y);
                var n = next.Get(x, y);
                int width = n.Width;

                if (nopt == CellDiffOption.Skip)
                {
                    // Out-of-band paint contract (R1 steal, ratatui Skip): never
                    // encoded. Advance one — a skipped wide lead lands on its
                    // tail next, which compares normally below.
                    x += 1;
                    continue;
                }

                if (nopt == CellDiffOption.ForcedWidth)
                {
                    // Explicit advance (image payloads — ratatui #2685 class):
                    // the reserved columns it covers are the widget's own, not
                    // the scan's to emit.
                    int fw = Math.Max(1, (int)next.GetForcedWidth(x, y));
                    if (prev.Get(x, y) != n)
                    {
                        Emit(ref count, prev, next, x, y);
                    }

                    x += fw;
                    continue;
                }

                // None skips equal cells; AlwaysUpdate emits even when equal
                // (remote mirrors, repaints).
                if (nopt == CellDiffOption.None && prev.Get(x, y) == n)
                {
                    x += Math.Max(1, width);
                    continue;
                }

                var f = prev.Get(x, y);
                if (width == Cell.Wide && n.Rune == f.Rune
                    && n.Style != f.Style && f.StyleVisibleOnBlank)
                {
                    // Style-only change on an unchanged wide glyph whose
                    // previous style was visible on blanks: trailing columns
                    // first (they still show the old style downstream), then
                    // the lead — ratatui #2652 order.
                    int end = Math.Min(x + width, cols);
                    for (int j = x + 1; j < end;)
                    {
                        j = EmitTrailing(ref count, prev, next, j, y, ref end, cols);
                    }

                    Emit(ref count, prev, next, x, y);
                    x += Math.Max(1, width);
                    continue;
                }

                if (width != Cell.Wide && f.Width > width && f.StyleVisibleOnBlank)
                {
                    // Narrow content replacing a visibly-styled wide glyph:
                    // the lead, then a force-refresh of every trailing column
                    // (downstream still shows the old style on blanks there).
                    Emit(ref count, prev, next, x, y);
                    int end = Math.Min(x + f.Width, cols);
                    for (int j = x + 1; j < end;)
                    {
                        j = EmitTrailing(ref count, prev, next, j, y, ref end, cols);
                    }

                    x += Math.Max(1, width);
                    continue;
                }

                Emit(ref count, prev, next, x, y);
                x += Math.Max(1, width);
            }
        }

        // Both copies below are NOT removable: _staging is a reused scratch
        // buffer retained across Encode calls (see remarks), so the batch must
        // own its array — and hints is caller-owned, retained by the batch for
        // sink fan-out and replay. Miss-shaped cost: bounded by the changed
        // cells of this frame, not the screen size.
        ImmutableArray<CellDiffMessage> changes = count == 0
            ? ImmutableArray<CellDiffMessage>.Empty
            : _staging[..count].ToImmutableArray();

        ImmutableArray<Rect> hintArray = useHints
            ? hints!.ToImmutableArray()
            : ImmutableArray<Rect>.Empty;

        return new CellDiffBatch(
            useHints ? CellDiffProtocolVersion.V2 : CellDiffProtocolVersion.V1,
            sequence,
            cols,
            rows,
            changes,
            hintArray);
    }

    /// <summary>Stages one change message, growing the retained scratch array.</summary>
    private void Emit(ref int count, ScreenBuffer prev, ScreenBuffer next, int x, int y)
    {
        if (count == _staging.Length)
        {
            Array.Resize(ref _staging, Math.Max(64, _staging.Length * 2));
        }

        _staging[count++] = new CellDiffMessage(x, y, prev.Get(x, y), next.Get(x, y));
    }

    /// <summary>
    /// Force-stages one trailing-range cell (R1 steal): every non-Skip cell in
    /// the range is emitted unconditionally — the previous wide glyph's style
    /// was visible on blanks, so downstream may show stale style there
    /// regardless of buffer equality. A wide cell inside the range covers its
    /// own trailing column too, so <paramref name="end"/> extends past it
    /// (clamped to <paramref name="cols"/>). Returns the next index to visit.
    /// </summary>
    private int EmitTrailing(ref int count, ScreenBuffer prev, ScreenBuffer next, int j, int y, ref int end, int cols)
    {
        int jw = Math.Max(1, (int)next.Get(j, y).Width);
        int advanced = j + jw;
        if (advanced > end)
        {
            end = Math.Min(advanced, cols);
        }

        if (next.GetDiffOption(j, y) != CellDiffOption.Skip)
        {
            Emit(ref count, prev, next, j, y);
        }

        return advanced;
    }

    private static bool HintAreaWithinThreshold(IReadOnlyList<Rect> hints, int cols, int rows)
    {
        long screenArea = (long)cols * rows;
        if (screenArea == 0)
        {
            return false;
        }

        long area = 0;
        for (int i = 0; i < hints.Count; i++)
        {
            Rect r = hints[i];
            area += (long)Math.Max(0, r.Width) * Math.Max(0, r.Height);
        }

        return area < screenArea * HintAreaThreshold;
    }
}
