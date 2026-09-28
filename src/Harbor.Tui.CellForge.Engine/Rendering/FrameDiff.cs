namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Scan mode of the frame-diff iterator (ENG1; Ratatui <c>diff.rs</c> pattern).
/// </summary>
public enum FrameDiffMode : byte
{
    /// <summary>
    /// Yield only cells whose content differs from FRONT. Unchanged cells and
    /// wide-tail halves are skipped internally and never surface
    /// (<c>Skip</c> semantics).
    /// </summary>
    Delta = 0,

    /// <summary>
    /// Yield every drawable cell regardless of equality (full repaint for
    /// remote mirrors and repaints where FRONT cannot be trusted). Wide-tail
    /// halves are still skipped — they are undrawable by construction — but
    /// FRONT still mirrors them so the post-drain invariant holds.
    /// </summary>
    AlwaysUpdate = 1,
}

/// <summary>
/// Zero-alloc frame-diff enumerable (ENG1): the compare half of the former
/// fused compare-and-emit scan, factored after Ratatui's <c>diff.rs</c>.
/// Enumerating yields each changed cell as <c>(X, Y, Target)</c> with no heap
/// allocation — a <c>ref struct</c> pair, so the enumerator cannot box, cannot
/// escape to the heap, and <c>foreach</c>/manual <c>while</c> drains lower to
/// plain index loops.
/// The draw half lives in <see cref="DiffEngine.Flush"/>: it consumes the
/// iterator and encodes through <see cref="AnsiWriter"/> (cached fg/bg via
/// the SGR automaton, skipped <c>MoveTo</c> on adjacent cells, single backend
/// write at <c>EndFrame</c> — the Terminal.Gui <c>OutputBase</c> pattern).
/// Byte contract: draining in <see cref="FrameDiffMode.Delta"/> mode and
/// drawing every yield through <c>MoveTo/SetStyle/PutRune</c> serializes the
/// exact same ANSI stream as the pre-ENG1 fused scan.
/// </summary>
/// <remarks>
/// Draining advances FRONT toward BACK as it goes (compare-and-mirror), so a
/// FULL drain upholds the post-flush invariant <c>FRONT == BACK</c>. A partial
/// drain leaves FRONT partially advanced — always drain to completion.
/// </remarks>
public readonly ref struct FrameDiff
{
    private readonly ScreenBuffer _front;
    private readonly ScreenBuffer _next;
    private readonly List<Rect>? _rects;
    private readonly PostFxPipeline? _fx;
    private readonly FrameDiffMode _mode;

    internal FrameDiff(
        ScreenBuffer front,
        ScreenBuffer next,
        List<Rect>? rects,
        PostFxPipeline? fx,
        FrameDiffMode mode)
    {
        _front = front;
        _next = next;
        _rects = rects;
        _fx = fx;
        _mode = mode;
    }

    /// <summary>Returns the zero-alloc cursor over the changed cells.</summary>
    public FrameDiffEnumerator GetEnumerator() =>
        new(_front, _next, _rects, _fx, _mode);
}

/// <summary>
/// Zero-alloc cursor over one <see cref="FrameDiff"/> scan. Either
/// <c>foreach</c> it or drive it manually
/// (<c>while (e.MoveNext()) { ... e.X ... e.Target ... }</c>); both lower
/// without allocation. <see cref="Target"/> is a 16-byte stack copy —
/// no heap involved. Row-hash fast path, hint spans, wide-pair repair and
/// row-hash adopt/invalidate behave exactly as the former fused scan.
/// </summary>
public ref struct FrameDiffEnumerator
{
    private readonly ScreenBuffer _front;
    private readonly ScreenBuffer _next;
    private readonly List<Rect>? _rects;
    private readonly PostFxPipeline? _fx;
    private readonly FrameDiffMode _mode;
    private readonly int _cols;
    private readonly int _rows;

    private int _rect;
    private int _x;
    private int _y;
    private int _rowRight;
    private bool _rowArmed;
    private bool _started;

    internal FrameDiffEnumerator(
        ScreenBuffer front,
        ScreenBuffer next,
        List<Rect>? rects,
        PostFxPipeline? fx,
        FrameDiffMode mode)
    {
        _front = front;
        _next = next;
        _rects = rects;
        _fx = fx;
        _mode = mode;
        _cols = front.Cols;
        _rows = front.Rows;
        _rect = 0;
        _x = 0;
        _y = 0;
        _rowRight = 0;
        _rowArmed = false;
        _started = false;
        X = -1;
        Y = -1;
        Target = Cell.Blank;
    }

    /// <summary>Column of the yielded cell.</summary>
    public int X { get; private set; }

    /// <summary>Row of the yielded cell.</summary>
    public int Y { get; private set; }

    /// <summary>
    /// Cell to draw at <c>(X, Y)</c> — already through the effect transform
    /// and already mirrored into FRONT. Valid only after a <c>true</c>
    /// <see cref="MoveNext"/> and before the next one.
    /// </summary>
    public Cell Target { get; private set; }

    /// <summary>
    /// Tuple view of the yield (<c>(X, Y, Target)</c>) for <c>foreach</c>
    /// destructuring — a stack-only <c>ValueTuple</c>, still zero-alloc.
    /// </summary>
    public (int X, int Y, Cell Cell) Current => (X, Y, Target);

    /// <summary>Advances to the next changed cell (<c>false</c> = scan complete).</summary>
    public bool MoveNext()
    {
        if (!_started)
        {
            _started = true;
            if (!OpenNextRow())
            {
                return false;
            }
        }

        while (true)
        {
            if (_x >= _rowRight)
            {
                CloseRow();
                if (!OpenNextRow())
                {
                    return false;
                }

                continue;
            }

            ref readonly Cell n = ref _next.At(_x, _y);
            int width = n.Width;

            if (width == Cell.WSkip)
            {
                // Tail half: never yielded (the terminal advances by itself),
                // but FRONT must mirror it silently. A hint boundary can enter
                // the row ON the tail half; repair the lead too so a wide pair
                // is never half-mirrored (no ghost glyphs).
                Cell f = _front.At(_x, _y);
                if (f != n)
                {
                    _front.At(_x, _y) = n;
                }

                if (_x > 0)
                {
                    ref readonly Cell leadNext = ref _next.At(_x - 1, _y);
                    var leadTarget = DiffEngine.ApplyFx(_fx, _x - 1, _y, in leadNext);
                    if (_front.At(_x - 1, _y) != leadTarget)
                    {
                        X = _x - 1;
                        Y = _y;
                        Target = leadTarget;
                        _front.At(_x - 1, _y) = leadTarget;
                        if (leadNext.Width == Cell.Wide)
                        {
                            _front.At(_x, _y) = Cell.WideTail;
                        }

                        _x += 1;
                        return true;
                    }
                }

                _x += 1;
                continue;
            }

            var target = DiffEngine.ApplyFx(_fx, _x, _y, in n);

            if (_mode == FrameDiffMode.Delta && _front.At(_x, _y) == target)
            {
                _x += width;
                continue;
            }

            X = _x;
            Y = _y;
            Target = target;
            _front.At(_x, _y) = target;
            if (width == Cell.Wide)
            {
                _front.At(_x + 1, _y) = Cell.WideTail;
            }

            _x += width;
            return true;
        }
    }

    private bool OpenNextRow()
    {
        if (_rects is null)
        {
            // Hintless single-span mode: rows run top to bottom, then done.
            while (_y < _rows)
            {
                if (TryOpenFullRow(_y))
                {
                    return true;
                }

                _y++;
            }

            return false;
        }

        while (_rect < _rects.Count)
        {
            var rect = _rects[_rect];
            int top = Math.Max(rect.Y, 0);
            int bottom = Math.Min(rect.Bottom, _rows);
            if (_y < top)
            {
                _y = top;
            }

            if (_y >= bottom)
            {
                // Rows are per-rect: the next rect restarts at its own top
                // (disjoint hints may share rows — each scans independently,
                // exactly like the former per-rect ScanRange calls; rescans
                // are idempotent since FRONT already mirrors).
                _rect++;
                _y = int.MinValue;
                continue;
            }

            int x1 = Math.Max(rect.X, 0);
            _rowRight = Math.Min(rect.Right, _cols);
            _rowArmed = _fx is { Count: > 0 };
            if (x1 >= _rowRight)
            {
                // Empty horizontal span (e.g. fully off-screen rect) — still
                // runs CloseRow so hash bookkeeping matches the fused scan.
                _x = x1;
                return true;
            }

            if (_mode == FrameDiffMode.Delta
                && _front.IsRowHashValid(_y) && _next.IsRowHashValid(_y)
                && _front.RowHash[_y] == _next.RowHash[_y])
            {
                // Row-hash fast path: both sides validated & identical —
                // nothing to do (adopt is skipped exactly as before: the
                // hashes are already equal and valid).
                _y++;
                if (_y >= bottom)
                {
                    _rect++;
                    _y = int.MinValue;
                }

                continue;
            }

            _x = x1;
            return true;
        }

        return false;
    }

    private bool TryOpenFullRow(int y)
    {
        _rowRight = _cols;
        _rowArmed = _fx is { Count: > 0 };
        if (_mode == FrameDiffMode.Delta
            && _front.IsRowHashValid(y) && _next.IsRowHashValid(y)
            && _front.RowHash[y] == _next.RowHash[y])
        {
            return false;
        }

        _x = 0;
        return true;
    }

    private void CloseRow()
    {
        // A row is identical to next across the scanned span. Adopt next's
        // authoritative hash only when the span covered the whole row; a
        // partial-row (hinted) scan invalidates FRONT's cache instead — cells
        // outside the span may still differ. With effects armed FRONT holds
        // transformed cells whose hashes differ from BACK's raw hashes —
        // invalidate instead of adopting.
        int x1 = _rects is null ? 0 : _rects[_rect].X;
        if (x1 <= 0 && _rowRight >= _cols && !_rowArmed)
        {
            _front.AdoptRowHash(_next, _y);
        }
        else
        {
            _front.MarkRowDirty(_y);
        }

        _y++;
        if (_rects is not null && _y >= Math.Min(_rects[_rect].Bottom, _rows))
        {
            _rect++;
            _y = int.MinValue;
        }
    }
}
