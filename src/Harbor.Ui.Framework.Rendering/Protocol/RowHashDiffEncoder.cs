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
    /// <summary>Hints above this share of the screen fall back to full scan (matches DiffEngine).</summary>
    public const double HintAreaThreshold = 0.25;

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
            // computed lazily and cached inside each buffer.
            if (!useHints && prev.RowHashCode(y) == next.RowHashCode(y))
            {
                continue;
            }

            for (int x = 0; x < cols; x++)
            {
                if (prev.Get(x, y) != next.Get(x, y))
                {
                    if (count == _staging.Length)
                    {
                        Array.Resize(ref _staging, Math.Max(64, _staging.Length * 2));
                    }

                    _staging[count++] = new CellDiffMessage(x, y, prev.Get(x, y), next.Get(x, y));
                }
            }
        }

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
