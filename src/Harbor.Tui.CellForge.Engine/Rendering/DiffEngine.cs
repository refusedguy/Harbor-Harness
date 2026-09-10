using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Cell-diff core (celldiff §2): fused full-scan prev→next that emits ANSI
/// straight into the writer — no intermediate change lists, zero steady-state
/// allocations. Three cooperating accelerations:
/// <list type="bullet">
///   <item><description>row-hash fast-path — silent rows cost O(1) instead of
///     O(cols);</description></item>
///   <item><description>FrameHint damage rects — point updates (spinner,
///     caret blink) scan only hinted area while it stays under 25 % of the
///     screen, otherwise the engine falls back to the full scan;</description></item>
///   <item><description>cursor elision + SGR delta are delegated to the
///     writer.</description></item>
/// </list>
/// After <see cref="Flush"/> the invariant <c>FRONT == BACK</c> holds
/// unconditionally (fuzz-tested).
/// </summary>
public sealed class DiffEngine
{
    /// <summary>Hints above this share of the screen fall back to full scan.</summary>
    public const double HintAreaThreshold = 0.25;

    private ScreenBuffer _front;
    private readonly List<Rect> _hints = new(16);

    public DiffEngine(int cols, int rows) => _front = new ScreenBuffer(cols, rows);

    public DiffEngine(ScreenBuffer front) => _front = front;

    /// <summary>The terminal-mirror buffer.</summary>
    public ScreenBuffer Front => _front;

    /// <summary>
    /// Hot-swap hook (renderer-moat T2): replaces the terminal-mirror buffer
    /// without touching the hint ledger. The caller must reconcile geometry
    /// with the paired BACK buffer before the next <see cref="Flush" /> —
    /// <see cref="ScreenSession.AdoptPendingSwap"/> does this by resizing and
    /// invalidating both grids as part of the atomic frame-boundary adoption.
    /// </summary>
    public void SwapFront(ScreenBuffer front)
    {
        ArgumentNullException.ThrowIfNull(front);
        _front = front;
    }

    /// <summary>
    /// Armed post-render effect pipeline (renderer-moat T3, internal hook —
    /// hosts arm it through <see cref="ScreenSession.Effects"/>). While
    /// active, the scan compares and mirrors cells THROUGH the effect
    /// transform: FRONT keeps mirroring what the terminal actually shows, so
    /// glow converges by construction — the frame the effect disappears, the
    /// plain cell differs from the mirrored glow and is repainted once.
    /// Null/empty pipeline → the exact classic scan (byte-identical).
    /// </summary>
    public PostFxPipeline? Effects { get; set; }

    /// <summary>
    /// Registers a damaged region for the next flush. The rect is clipped to
    /// the screen and merged into any hint it overlaps — the union may cover
    /// cells neither rect damaged (conservative), never fewer. Damage outside
    /// registered hints is NOT scanned: callers must hint every region a
    /// frame might have touched, or skip hints entirely for that frame.
    /// </summary>
    public void FrameHint(in Rect damage)
    {
        var clipped = damage.Intersect(new Rect(0, 0, _front.Cols, _front.Rows));
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return;
        }

        var hints = _hints;
        for (int i = 0; i < hints.Count; i++)
        {
            if (hints[i].Intersect(clipped) != default)
            {
                hints[i] = Union(hints[i], clipped);
                return;
            }
        }

        hints.Add(clipped);
    }

    /// <summary>Drops accumulated hints (e.g. on resize).</summary>
    public void ClearHints() => _hints.Clear();

    /// <summary>Total hinted cell count clipped to the screen.</summary>
    internal long HintArea()
    {
        long area = 0;
        var screen = new Rect(0, 0, _front.Cols, _front.Rows);
        foreach (var hint in _hints)
        {
            area += hint.Intersect(screen).Area;
        }

        return area;
    }

    /// <summary>
    /// Syncs the terminal to <paramref name="next"/>: emits the changed cells
    /// through the writer and advances FRONT. Geometry must match. When hints
    /// are registered and their clipped area stays under
    /// <see cref="HintAreaThreshold"/> of the screen, only hinted regions are
    /// scanned; any other frame runs the fused full scan.
    /// </summary>
    public void Flush(ScreenBuffer next, AnsiWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(next.Cols, _front.Cols);
        ArgumentOutOfRangeException.ThrowIfNotEqual(next.Rows, _front.Rows);

        bool useHints = _hints.Count > 0
            && HintArea() < (long)_front.Cols * _front.Rows * HintAreaThreshold;

        if (useHints)
        {
            // ScanRange never re-enters FrameHint, so the live list can be
            // scanned directly and dropped afterwards — no snapshot alloc.
            // Sorting row-major keeps the hinted emission order identical to
            // the fused full scan (top→bottom, left→right per row), so both
            // paths serialize the same changed cells into the same ANSI
            // stream — the byte-identical golden contract.
            var hints = _hints;
            if (hints.Count > 1)
            {
                hints.Sort(static (a, b) => a.Y != b.Y
                    ? a.Y.CompareTo(b.Y)
                    : a.X.CompareTo(b.X));
            }

            for (int i = 0; i < hints.Count; i++)
            {
                var rect = hints[i];
                ScanRange(rect.X, rect.Y, rect.Right, rect.Bottom, next, writer);
            }
        }
        else
        {
            ScanRange(0, 0, _front.Cols, _front.Rows, next, writer);
        }

        _hints.Clear();
    }

    // ── Core scan ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fused compare-and-emit over [x1..x2) × [y1..y2). With an armed effect
    /// pipeline the comparison runs against the TRANSFORMED next cell and
    /// FRONT mirrors the transformed cell (it mirrors the terminal, which the
    /// effects have recolored) — so disarming converges in one plain repaint.
    /// The unarmed path is the exact classic scan: same compares, same bytes,
    /// zero added work.
    /// </summary>
    private void ScanRange(int x1, int y1, int x2, int y2, ScreenBuffer next, AnsiWriter writer)
    {
        int cols = _front.Cols;
        int rows = _front.Rows;
        int right = Math.Min(x2, cols);
        int bottom = Math.Min(y2, rows);
        var pipeline = Effects;
        bool armed = pipeline is { Count: > 0 };

        for (int y = Math.Max(0, y1); y < bottom; y++)
        {
            // Row-hash fast path: both sides validated & identical → nothing
            // to do; adopt next's hash into front (they are equal). Safe with
            // effects armed too: equal hashes mean FRONT's transformed cells
            // already equal BACK's raw cells (identity transform on the row).
            if (_front.IsRowHashValid(y) && next.IsRowHashValid(y)
                && _front.RowHash[y] == next.RowHash[y])
            {
                continue;
            }

            for (int x = Math.Max(0, x1); x < right; )
            {
                ref readonly Cell n = ref next.At(x, y);
                Cell f = _front.At(x, y);
                int width = n.Width;

                if (width == Cell.WSkip)
                {
                    // Tail half: never emitted (terminal advances by itself),
                    // but FRONT must mirror it silently. A hint boundary can
                    // enter the row ON the tail half; repair the lead too so
                    // a wide pair is never half-mirrored (no ghost glyphs).
                    if (f != n)
                    {
                        _front.At(x, y) = n;
                    }

                    if (x > 0)
                    {
                        ref readonly Cell leadNext = ref next.At(x - 1, y);
                        var leadTarget = Fx(pipeline, x - 1, y, in leadNext);
                        if (_front.At(x - 1, y) != leadTarget)
                        {
                            writer.MoveTo(x - 1, y);
                            writer.SetStyle(leadTarget.Style);
                            writer.PutRune(new Rune(leadTarget.Rune));
                            _front.At(x - 1, y) = leadTarget;
                            if (leadNext.Width == Cell.Wide)
                            {
                                _front.At(x, y) = Cell.WideTail;
                            }
                        }
                    }

                    x += 1;
                    continue;
                }

                // Post-render stage (renderer-moat T3): the diff-selected cell
                // passes through the effect transform — after cell selection,
                // before SGR encoding. Identity results emit no bytes (SGR
                // automaton dedupes).
                var target = Fx(pipeline, x, y, in n);

                if (f == target)
                {
                    x += width;
                    continue;
                }

                writer.MoveTo(x, y);
                writer.SetStyle(target.Style);
                writer.PutRune(new Rune(target.Rune));

                _front.At(x, y) = target;
                if (width == Cell.Wide)
                {
                    _front.At(x + 1, y) = Cell.WideTail;
                }

                x += width;
            }

            // Row is now identical to next across the scanned span. Adopt
            // next's authoritative hash only when the span covered the whole
            // row; a partial-row (hinted) scan must invalidate FRONT's cache
            // instead — cells outside the span may still differ, and next's
            // stored hash may be stale from before this frame's paint. With
            // effects armed FRONT holds transformed cells whose hashes differ
            // from BACK's raw hashes — invalidate instead of adopting.
            if (x1 <= 0 && right >= cols && !armed)
            {
                _front.AdoptRowHash(next, y);
            }
            else
            {
                _front.MarkRowDirty(y);
            }
        }
    }

    /// <summary>Applies the armed pipeline (null → identity) to one cell.
    /// JIT-inlined null check keeps the unarmed hot path at zero added cost.</summary>
    private static Cell Fx(PostFxPipeline? pipeline, int x, int y, in Cell cell) =>
        pipeline is null ? cell : pipeline.Transform(x, y, in cell);

    /// <summary>Smallest rect covering both inputs (hint-union merge).</summary>
    private static Rect Union(Rect a, Rect b)
    {
        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    // ── Verification helpers ───────────────────────────────────────────────

    /// <summary>True when FRONT equals NEXT cell-for-cell over the whole screen
    /// (the post-flush invariant; also the paranoid hint check). With an armed
    /// effect pipeline FRONT holds transformed (terminal-view) cells, so the
    /// comparison is against the transformed look, not the raw paint.</summary>
    public bool FrontMatches(ScreenBuffer next)
    {
        if (next.Cols != _front.Cols || next.Rows != _front.Rows)
        {
            return false;
        }

        for (int y = 0; y < next.Rows; y++)
        {
            for (int x = 0; x < next.Cols; x++)
            {
                if (_front.Get(x, y) != next.Get(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
