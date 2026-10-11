
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
    /// Per-cell <see cref="CellDiffOption.Skip"/> wins over the mode: Skip
    /// cells are painted out-of-band, so even a full repaint must not emit
    /// them (they are still mirrored silently).
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

    // R1 steal (epic #1155): ratatui TrailingState port — pending trailing
    // columns of a wide-glyph update. Armed mid-row, drained at the head of
    // the next MoveNext before the main scan resumes; ranges never cross rows
    // (clamped to _rowRight, strictly tighter than ratatui's buffer-len clamp).
    private bool _trailingActive;
    private int _trailingNext;
    private int _trailingEnd;
    private bool _trailingForce;
    private int _deferredX;

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
        _trailingActive = false;
        _trailingNext = 0;
        _trailingEnd = 0;
        _trailingForce = false;
        _deferredX = -1;
        X = -1;
        Y = -1;
        Target = Cell.Blank;
        Advance = 1;
        IsForcedWidth = false;
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
    /// Cursor advance the writer must apply after drawing
    /// <see cref="Target"/>: 1 for narrow cells, 2 for wide leads, the armed
    /// width for <see cref="CellDiffOption.ForcedWidth"/> cells.
    /// </summary>
    public int Advance { get; private set; }

    /// <summary>
    /// True when this yield carries an explicit
    /// <see cref="CellDiffOption.ForcedWidth"/> advance — Flush paints it via
    /// <c>PutRuneWidth</c> and advances its adjacency bookkeeping by
    /// <see cref="Advance"/>, not by one.
    /// </summary>
    public bool IsForcedWidth { get; private set; }

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
            // Pending trailing range first (R1 steal, ratatui TrailingState):
            // a wide-glyph update armed this before its lead was (re)painted.
            if (_trailingActive && DrainTrailing())
            {
                return true;
            }

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
            var option = _next.GetDiffOption(_x, _y);

            if (option == CellDiffOption.Skip)
            {
                // Out-of-band paint contract: never yielded, mirrored silently
                // so the post-drain FRONT == BACK invariant holds. Advance one
                // (ratatui parity — Skip consumes no width); a skipped wide
                // lead lands on its tail next, which the WSkip arm mirrors.
                MirrorSilent(_x);
                _x += 1;
                continue;
            }

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

                _front.MirrorDiffOption(_x, _y, option, _next.GetForcedWidth(_x, _y));

                if (_x > 0)
                {
                    ref readonly Cell leadNext = ref _next.At(_x - 1, _y);
                    var leadTarget = DiffEngine.ApplyFx(_fx, _x - 1, _y, in leadNext);
                    if (_front.At(_x - 1, _y) != leadTarget)
                    {
                        X = _x - 1;
                        Y = _y;
                        Target = leadTarget;
                        Advance = Math.Max(1, (int)leadNext.Width);
                        IsForcedWidth = false;
                        _front.At(_x - 1, _y) = leadTarget;
                        _front.MirrorDiffOption(_x - 1, _y, _next.GetDiffOption(_x - 1, _y), _next.GetForcedWidth(_x - 1, _y));
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

            if (option == CellDiffOption.ForcedWidth)
            {
                // Explicit advance (image-protocol payloads): yield when
                // different, then skip width - 1 reserved columns — mirrored
                // silently so FRONT == BACK holds even though they are never
                // painted (ratatui #2685 class: the advance must not hide
                // later cells, and the mirror must not go stale).
                int fw = Math.Max(1, (int)_next.GetForcedWidth(_x, _y));
                var forcedTarget = DiffEngine.ApplyFx(_fx, _x, _y, in n);
                bool changed = _front.At(_x, _y) != forcedTarget;
                _front.At(_x, _y) = forcedTarget;
                _front.MirrorDiffOption(_x, _y, option, (ushort)Math.Min(fw, ushort.MaxValue));
                int reservedEnd = Math.Min(_x + fw, _rowRight);
                for (int j = _x + 1; j < reservedEnd; j++)
                {
                    MirrorSilent(j);
                }

                int leadX = _x;
                _x += fw;
                if (!changed && _mode == FrameDiffMode.Delta)
                {
                    continue;
                }

                X = leadX;
                Y = _y;
                Target = forcedTarget;
                Advance = fw;
                IsForcedWidth = true;
                return true;
            }

            var target = DiffEngine.ApplyFx(_fx, _x, _y, in n);

            if (_mode == FrameDiffMode.Delta && option == CellDiffOption.None && _front.At(_x, _y) == target)
            {
                _x += Math.Max(1, width);
                continue;
            }

            Cell frontCell = _front.At(_x, _y);
            int prevWidth = frontCell.Width;
            bool prevVisible = frontCell.StyleVisibleOnBlank;

            // Style-only change on an unchanged wide glyph whose previous
            // style was visible on blanks: clear the trailing columns FIRST
            // (the terminal still shows the old style there), then repaint the
            // lead. Trailing-before-lead: clearing after can erase the glyph
            // on some terminals (ratatui #2652).
            if (width == Cell.Wide && n.Rune == frontCell.Rune
                && target.Style != frontCell.Style && prevVisible)
            {
                ArmTrailing(_x + 1, Math.Min(_x + width, _rowRight), force: true, deferredX: _x);
                continue;
            }

            if (width != Cell.Wide && prevWidth > width && prevVisible)
            {
                // Narrow content replacing a visibly-styled wide glyph: the
                // terminal still shows the old style on the trailing columns
                // even though the buffer holds blanks there — force-refresh
                // every cell in the trailing range after the lead below.
                ArmTrailing(_x + 1, Math.Min(_x + prevWidth, _rowRight), force: true, deferredX: -1);
            }

            int advance = Math.Max(1, width);
            X = _x;
            Y = _y;
            Target = target;
            Advance = advance;
            IsForcedWidth = false;
            _front.At(_x, _y) = target;
            _front.MirrorDiffOption(_x, _y, option, _next.GetForcedWidth(_x, _y));
            if (width == Cell.Wide)
            {
                _front.At(_x + 1, _y) = Cell.WideTail;
                _front.MirrorDiffOption(_x + 1, _y, _next.GetDiffOption(_x + 1, _y), _next.GetForcedWidth(_x + 1, _y));
            }

            _x += advance;
            return true;
        }
    }

    /// <summary>
    /// Arms a pending trailing range (R1 steal, ratatui TrailingState): columns
    /// <c>[next, end)</c> drain before the main scan resumes; a non-negative
    /// <paramref name="deferredX"/> repaints that lead cell after the drain.
    /// Both bounds are row-relative and pre-clamped to the row span.
    /// </summary>
    private void ArmTrailing(int next, int end, bool force, int deferredX)
    {
        _trailingActive = true;
        _trailingNext = next;
        _trailingEnd = end;
        _trailingForce = force;
        _deferredX = deferredX;
    }

    /// <summary>
    /// Drains one cell from the armed trailing range (true = yielded, drive
    /// <see cref="X"/>/<see cref="Y"/>/<see cref="Target"/>); false = range
    /// exhausted (plus a possible deferred-lead yield — also true), resume the
    /// main scan. Force mode emits every non-Skip cell unconditionally: the
    /// previous wide glyph's style was visible on blanks, so the terminal may
    /// show stale style there regardless of buffer equality.
    /// </summary>
    private bool DrainTrailing()
    {
        while (_trailingNext < _trailingEnd)
        {
            int j = _trailingNext;
            // Advance past this cell; a wide cell inside the range covers its
            // own trailing column too, so extend the end past it (clamped) —
            // otherwise the main scan would write EMPTY over the just-drawn
            // glyph's right half.
            ref readonly Cell jc = ref _next.At(j, _y);
            int jw = Math.Max(1, (int)jc.Width);
            _trailingNext += jw;
            if (_trailingNext > _trailingEnd)
            {
                _trailingEnd = Math.Min(_trailingNext, _rowRight);
            }

            var trailingOption = _next.GetDiffOption(j, _y);
            if (trailingOption == CellDiffOption.Skip)
            {
                MirrorSilent(j);
                continue;
            }

            var t = DiffEngine.ApplyFx(_fx, j, _y, in jc);
            if (!_trailingForce && _front.At(j, _y) == t)
            {
                continue;
            }

            X = j;
            Y = _y;
            Target = t;
            Advance = jw;
            IsForcedWidth = false;
            _front.At(j, _y) = t;
            _front.MirrorDiffOption(j, _y, trailingOption, _next.GetForcedWidth(j, _y));
            if (jw == Cell.Wide && j + 1 < _rowRight)
            {
                _front.At(j + 1, _y) = Cell.WideTail;
                _front.MirrorDiffOption(j + 1, _y, _next.GetDiffOption(j + 1, _y), _next.GetForcedWidth(j + 1, _y));
            }

            return true;
        }

        _x = _trailingEnd;
        _trailingActive = false;
        if (_deferredX < 0)
        {
            return false;
        }

        int lx = _deferredX;
        _deferredX = -1;
        ref readonly Cell ln = ref _next.At(lx, _y);
        var lt = DiffEngine.ApplyFx(_fx, lx, _y, in ln);
        X = lx;
        Y = _y;
        Target = lt;
        Advance = Math.Max(1, (int)ln.Width);
        IsForcedWidth = false;
        _front.At(lx, _y) = lt;
        _front.MirrorDiffOption(lx, _y, _next.GetDiffOption(lx, _y), _next.GetForcedWidth(lx, _y));
        if (ln.Width == Cell.Wide && lx + 1 < _cols)
        {
            _front.At(lx + 1, _y) = Cell.WideTail;
            _front.MirrorDiffOption(lx + 1, _y, _next.GetDiffOption(lx + 1, _y), _next.GetForcedWidth(lx + 1, _y));
        }

        return true;
    }

    /// <summary>Mirrors one NEXT cell into FRONT (content + directive), no yield.</summary>
    private void MirrorSilent(int x)
    {
        _front.At(x, _y) = _next.At(x, _y);
        _front.MirrorDiffOption(x, _y, _next.GetDiffOption(x, _y), _next.GetForcedWidth(x, _y));
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
                && _front.RowHash[_y] == _next.RowHash[_y]
                && !_front.HasAlwaysUpdate(_y) && !_next.HasAlwaysUpdate(_y))
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
            && _front.RowHash[y] == _next.RowHash[y]
            && !_front.HasAlwaysUpdate(y) && !_next.HasAlwaysUpdate(y))
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
