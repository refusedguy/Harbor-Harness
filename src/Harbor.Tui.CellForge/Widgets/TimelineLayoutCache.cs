namespace Harbor.Tui.CellForge.Widgets;

/// <summary>What <see cref="TimelineLayoutCache.PrepareLayout"/> did this frame.</summary>
public enum LayoutOutcome : byte
{
    /// <summary>Nothing changed — reuse everything.</summary>
    Unchanged = 0,

    /// <summary>Suffix patched (append/stream-dirty/settle) — work bounded by the changed suffix.</summary>
    Patched = 1,

    /// <summary>Width change — measurements reset, estimates rebuilt, anchor re-pins the viewport.</summary>
    FullRebuild = 2,
}

/// <summary>
/// Virtual-timeline layout math (widgets §3.3, grok prepare_layout ×3-case):
/// cheap estimates everywhere, EXACT heights settled only for blocks inside
/// the viewport, monotonic virtual_y prefix array for binary search, scroll
/// anchor against width-change jumps. Arrays grow geometrically and are
/// reused — steady-state frames allocate nothing.
///
/// Case 1 — width changed: forget measurements, re-estimate all, rebuild.
/// Case 2 — appends / dirty heights / replacements: re-estimate the affected
///          suffix, patch virtual_y from the first touched index (O(1) for
///          the streaming tail).
/// Case 3 — nothing structural: totals and ranges served from cache, settle
///          any newly visible blocks.
/// </summary>
public sealed class TimelineLayoutCache
{
    private const int InitialSlots = 64;

    private readonly struct Slot(int exactH, int estH, bool measured)
    {
        public int ExactH { get; } = exactH;
        public int EstH { get; } = estH;
        public bool Measured { get; } = measured;

        public static Slot Estimated(int est) => new(-1, Math.Max(1, est), false);
        public static Slot ExactMeasured(int h) => new(h, h, true);
    }

    private IChatBlock[] _blocks = [];
    private Slot[] _slots = [];
    private long[] _virtual = [0]; // _virtual[i] = top row of block i; [_count] = total height
    private int _count;

    private int _width = -1;
    private int _unmeasuredFrom;               // first index lacking any height info
    private int _dirtyFrom = int.MaxValue;     // first index whose cached height may be stale
    private int _measureCallsThisFrame;

    // Scroll anchor: block identity + row within it, captured before rebuilds.
    private IChatBlock? _anchorBlock;
    private int _anchorRow;
    private long _anchorY;

    public int Count => _count;

    public long TotalHeight => _virtual[_count];

    public IChatBlock BlockAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        return _blocks[index];
    }

    /// <summary>Measure() calls issued during the last <see cref="PrepareLayout"/>.</summary>
    public int MeasureCallsLastFrame => _measureCallsThisFrame;

    public void Append(IChatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        EnsureCapacity(_count + 1);
        _blocks[_count] = block;
        _slots[_count] = default;
        _count++;
        EnsureVirtualLength(_count + 1);

        _unmeasuredFrom = Math.Min(_unmeasuredFrom, _count - 1);
        _dirtyFrom = Math.Min(_dirtyFrom, _count - 1);
    }

    /// <summary>Drops the oldest block; subsequent indices shift one left.</summary>
    public bool EvictFirst()
    {
        if (_count == 0)
        {
            return false;
        }

        var evicted = _blocks[0];
        if (_count > 1)
        {
            Array.Copy(_blocks, 1, _blocks, 0, _count - 1);
            Array.Copy(_slots, 1, _slots, 0, _count - 1);
        }

        _count--;
        _blocks[_count] = null!;
        _unmeasuredFrom = Math.Max(0, _unmeasuredFrom - 1);
        _dirtyFrom = Math.Min(_dirtyFrom, 0);

        if (ReferenceEquals(_anchorBlock, evicted))
        {
            _anchorBlock = null;
        }

        return true;
    }

    /// <summary>Streaming tail grew or a mutable card mutated — heights from
    /// here are stale. Already-measured slots in the range are demoted back
    /// to estimates so the next settle re-measures them.</summary>
    public void MarkHeightsDirty(int fromIndex)
    {
        if ((uint)fromIndex > (uint)_count)
        {
            return;
        }

        for (int i = fromIndex; i < _count; i++)
        {
            ref var s = ref _slots[i];
            if (s.Measured)
            {
                s = default;
            }
        }

        _unmeasuredFrom = Math.Min(_unmeasuredFrom, Math.Max(fromIndex, 0));
        _dirtyFrom = Math.Min(_dirtyFrom, fromIndex);
    }

    /// <summary>Swaps a live-stream placeholder for the committed block in place.</summary>
    public void Replace(int index, IChatBlock block)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        ArgumentNullException.ThrowIfNull(block);

        _blocks[index] = block;
        _slots[index] = default;
        _unmeasuredFrom = Math.Min(_unmeasuredFrom, index);
        _dirtyFrom = Math.Min(_dirtyFrom, index);
    }

    /// <summary>Captures the viewport top so a width-change rebuild can restore it.</summary>
    public void PinAnchor(long scrollTopY)
    {
        if (_count == 0)
        {
            return;
        }

        long maxTop = Math.Max(0, TotalHeight - 1);
        int idx = EntryAtY(Math.Clamp(scrollTopY, 0, maxTop));
        _anchorBlock = _blocks[idx];
        _anchorRow = (int)Math.Clamp(scrollTopY - _virtual[idx], 0, Math.Max(0, EffectiveHeight(idx) - 1));
        _anchorY = scrollTopY;
    }

    /// <summary>
    /// Runs the 3-case layout for the frame. After a full rebuild call
    /// <see cref="RestoreAnchor"/> to de-jump the viewport.
    /// </summary>
    public LayoutOutcome PrepareLayout(int width, int viewportH, long scrollY)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportH);
        _measureCallsThisFrame = 0;

        // ── Case 1: width changed ───────────────────────────────────────────
        if (width != _width)
        {
            _width = width;
            Array.Clear(_slots, 0, _count); // wrapped heights are invalid at the new width
            _unmeasuredFrom = 0;
            _dirtyFrom = 0;
            ComputeEstimates();
            PatchVirtualFrom(0);
            SettleVisible(viewportH, scrollY);
            _dirtyFrom = int.MaxValue;
            return LayoutOutcome.FullRebuild;
        }

        // ── Case 2: structure or heights changed ────────────────────────────
        if (_dirtyFrom != int.MaxValue)
        {
            ComputeEstimates();
            PatchVirtualFrom(Math.Min(_dirtyFrom, _count));
            _dirtyFrom = int.MaxValue;
            SettleVisible(viewportH, scrollY);
            return LayoutOutcome.Patched;
        }

        // ── Case 3: cache hit; settle newly visible blocks only ─────────────
        return SettleVisible(viewportH, scrollY) ? LayoutOutcome.Patched : LayoutOutcome.Unchanged;
    }

    /// <summary>Post-rebuild scroll fix-up: keeps the anchored block at its row.</summary>
    public long RestoreAnchor()
    {
        if (_anchorBlock is null || _count == 0)
        {
            return _anchorY;
        }

        for (int i = 0; i < _count; i++)
        {
            if (ReferenceEquals(_blocks[i], _anchorBlock))
            {
                return _virtual[i] + _anchorRow;
            }
        }

        return _anchorY; // anchored block was evicted — caller clamps
    }

    /// <summary>Index of the block whose span contains row <paramref name="y"/> (binary search).</summary>
    public int EntryAtY(long y)
    {
        if (_count == 0)
        {
            return -1;
        }

        int lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_virtual[mid] <= y)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>Inclusive range of blocks intersecting rows [scrollY, scrollY+viewportH).</summary>
    public (int First, int Last) VisibleRange(long scrollY, int viewportH)
    {
        if (_count == 0 || viewportH <= 0)
        {
            return (-1, -2);
        }

        int first = EntryAtY(Math.Max(0, scrollY));
        int last = first;
        while (last < _count && _virtual[last] < scrollY + viewportH)
        {
            last++;
        }

        return (first, last - 1);
    }

    public long BlockTop(int index) => _virtual[index];

    public int EffectiveHeight(int index)
    {
        ref readonly var s = ref _slots[index];
        return s.Measured ? s.ExactH : s.EstH;
    }

    private void ComputeEstimates()
    {
        for (int i = _unmeasuredFrom; i < _count; i++)
        {
            ref var s = ref _slots[i];
            if (!s.Measured && s.EstH == 0)
            {
                s = Slot.Estimated(_blocks[i].CheapEstimate(_width));
            }
        }

        _unmeasuredFrom = _count;
    }

    private void PatchVirtualFrom(int from)
    {
        if (from >= _count)
        {
            if (_count >= 0)
            {
                RecomputeTailTotal();
            }

            return;
        }

        long sum = from == 0 ? 0 : _virtual[from - 1] + EffectiveHeight(from - 1);
        for (int i = from; i < _count; i++)
        {
            _virtual[i] = sum;
            sum += EffectiveHeight(i);
        }

        _virtual[_count] = sum;
    }

    /// <summary>Total-only fix-up when the suffix start is past the end (e.g. pure eviction).</summary>
    private void RecomputeTailTotal()
    {
        long sum = _count > 0 ? _virtual[_count - 1] + EffectiveHeight(_count - 1) : 0;
        _virtual[_count] = sum;
    }

    /// <summary>Measures previously-unmeasured blocks inside the viewport; patches if any.</summary>
    private bool SettleVisible(int viewportH, long scrollY)
    {
        if (_count == 0 || viewportH <= 0)
        {
            return false;
        }

        var (first, last) = VisibleRange(scrollY, viewportH);
        if (first < 0)
        {
            return false;
        }

        bool changed = false;
        int patchFrom = int.MaxValue;
        for (int i = first; i <= last && i < _count; i++)
        {
            ref var s = ref _slots[i];
            if (!s.Measured)
            {
                var m = _blocks[i].Measure(_width);
                s = m.IsExact ? Slot.ExactMeasured(m.MaxLines) : Slot.Estimated(m.BestGuess);
                _measureCallsThisFrame++;
                changed = true;
                patchFrom = Math.Min(patchFrom, i);
            }
        }

        if (changed)
        {
            PatchVirtualFrom(patchFrom);
        }

        return changed;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _blocks.Length)
        {
            return;
        }

        int cap = Math.Max(InitialSlots, _blocks.Length * 2);
        while (cap < needed)
        {
            cap *= 2;
        }

        Array.Resize(ref _blocks, cap);
        Array.Resize(ref _slots, cap);
    }

    private void EnsureVirtualLength(int needed)
    {
        if (needed <= _virtual.Length)
        {
            return;
        }

        int cap = Math.Max(InitialSlots + 1, _virtual.Length * 2);
        while (cap < needed)
        {
            cap *= 2;
        }

        Array.Resize(ref _virtual, cap);
    }
}
