using System.Buffers;
using System.Text;
using Harbor.Ui.Framework.Rendering.Protocol;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Cell-diff core (celldiff §2): ENG1 split of the fused full-scan prev→next
/// into a zero-alloc diff iterator (<see cref="FrameDiff"/>, Ratatui
/// <c>diff.rs</c> pattern) plus a delta draw through the writer (Terminal.Gui
/// <c>OutputBase</c> pattern: cached fg/bg via the SGR automaton, skipped
/// <c>MoveTo</c> on adjacent cells, single backend write per frame at
/// <c>EndFrame</c>). Three cooperating accelerations:
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
    /// <summary>Hints above this share of the screen fall back to full scan (canonical value: <see cref="CellDiffHints.HintAreaThreshold"/>).</summary>
    public const double HintAreaThreshold = CellDiffHints.HintAreaThreshold;

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
    /// Syncs the terminal to <paramref name="next"/>: diffs through the
    /// zero-alloc <see cref="FrameDiff"/> iterator, draws the delta into the
    /// writer, and advances FRONT. Geometry must match. When hints are
    /// registered and their clipped area stays under
    /// <see cref="HintAreaThreshold"/> of the screen, only hinted regions are
    /// scanned; any other frame runs the full scan. The frame leaves the
    /// process through the writer's single backend write at
    /// <c>EndFrame</c> — <see cref="Flush"/> itself never touches the backend.
    /// </summary>
    public void Flush(ScreenBuffer next, AnsiWriter writer)
    {
        var cursor = Diff(next).GetEnumerator();

        // Adjacent-cell MoveTo skip (OutputBase pattern): after a drawn cell
        // the writer's pen already sits on the next column, so a MoveTo there
        // would elide inside the writer. Skipping the call outright is
        // byte-identical — MoveTo's pre-elision SGR flush is a no-op here
        // because SetStyle always leaves zero pending SGR params.
        int lastX = int.MinValue;
        int lastY = int.MinValue;
        while (cursor.MoveNext())
        {
            int x = cursor.X;
            int y = cursor.Y;
            Cell target = cursor.Target;
            if (x != lastX + 1 || y != lastY)
            {
                writer.MoveTo(x, y);
            }

            writer.SetStyle(target.Style);
            writer.PutRune(new Rune(target.Rune));
            lastX = x;
            lastY = y;
        }

        _hints.Clear();
    }

    /// <summary>
    /// Opens the zero-alloc diff of FRONT against <paramref name="next"/>
    /// (mode <see cref="FrameDiffMode.Delta"/> by default). Draining the
    /// returned <see cref="FrameDiff"/> yields each changed cell as
    /// <c>(X, Y, Target)</c> and mirrors FRONT as it goes — a full drain
    /// upholds <c>FRONT == BACK</c>. Hint lifecycle stays with
    /// <see cref="Flush"/>: this method sorts the registered hints
    /// row-major (no allocation) but does NOT clear them.
    /// </summary>
    public FrameDiff Diff(ScreenBuffer next, FrameDiffMode mode = FrameDiffMode.Delta)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(next.Cols, _front.Cols);
        ArgumentOutOfRangeException.ThrowIfNotEqual(next.Rows, _front.Rows);

        // Sorting row-major keeps the hinted emission order identical to the
        // full scan (top→bottom, left→right per row), so both paths serialize
        // the same changed cells into the same ANSI stream — the byte-identical
        // golden contract. List.Sort is allocation-free (introsort, no closure).
        bool useHints = _hints.Count > 0
            && HintArea() < (long)_front.Cols * _front.Rows * HintAreaThreshold;
        if (useHints && _hints.Count > 1)
        {
            _hints.Sort(static (a, b) => a.Y != b.Y
                ? a.Y.CompareTo(b.Y)
                : a.X.CompareTo(b.X));
        }

        return new FrameDiff(_front, next, useHints ? _hints : null, Effects, mode);
    }

    /// <summary>Applies the armed pipeline (null → identity) to one cell.
    /// JIT-inlined null check keeps the unarmed hot path at zero added cost.</summary>
    internal static Cell ApplyFx(PostFxPipeline? pipeline, int x, int y, in Cell cell) =>
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
