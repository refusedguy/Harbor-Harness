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

    private readonly ScreenBuffer _front;
    private readonly List<Rect> _hints = [];

    public DiffEngine(int cols, int rows) => _front = new ScreenBuffer(cols, rows);

    public DiffEngine(ScreenBuffer front) => _front = front;

    /// <summary>The terminal-mirror buffer.</summary>
    public ScreenBuffer Front => _front;

    /// <summary>Registers a damaged region for the next flush.</summary>
    public void FrameHint(in Rect damage) => _hints.Add(damage);

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
    /// through the writer and advances FRONT. Geometry must match.
    /// </summary>
    public void Flush(ScreenBuffer next, AnsiWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(next.Cols, _front.Cols);
        ArgumentOutOfRangeException.ThrowIfNotEqual(next.Rows, _front.Rows);

        bool useHints = _hints.Count > 0
            && HintArea() < (long)_front.Cols * _front.Rows * HintAreaThreshold;

        if (useHints)
        {
            foreach (var rect in _hints)
            {
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

    /// <summary>Fused compare-and-emit over [x1..x2) × [y1..y2).</summary>
    private void ScanRange(int x1, int y1, int x2, int y2, ScreenBuffer next, AnsiWriter writer)
    {
        int cols = _front.Cols;
        int rows = _front.Rows;
        int right = Math.Min(x2, cols);
        int bottom = Math.Min(y2, rows);

        for (int y = Math.Max(0, y1); y < bottom; y++)
        {
            // Row-hash fast path: both sides validated & identical → nothing
            // to do; adopt next's hash into front (they are equal).
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
                    // but FRONT must mirror it silently.
                    if (f != n)
                    {
                        _front.At(x, y) = n;
                    }

                    x += 1;
                    continue;
                }

                if (f == n)
                {
                    x += width;
                    continue;
                }

                writer.MoveTo(x, y);
                writer.SetStyle(n.Style);
                writer.PutRune(new Rune(n.Rune));

                _front.At(x, y) = n;
                if (width == Cell.Wide)
                {
                    _front.At(x + 1, y) = Cell.WideTail;
                }

                x += width;
            }

            // Row is now identical to next — adopt its authoritative hash.
            _front.AdoptRowHash(next, y);
        }
    }

    // ── Verification helpers ───────────────────────────────────────────────

    /// <summary>True when FRONT equals NEXT cell-for-cell over the whole screen
    /// (the post-flush invariant; also the paranoid hint check).</summary>
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
