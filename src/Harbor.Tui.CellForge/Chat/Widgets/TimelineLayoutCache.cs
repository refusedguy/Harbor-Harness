namespace Harbor.Tui.CellForge.Widgets;

using Harbor.Ui.Framework.Rendering.PerformanceContracts;

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
/// Case 1 — width changed: adopt the width's own measured heights when it was
///          laid out before (#412); otherwise every height is invalid at the
///          new width and each block is re-estimated once. Either way only the
///          visible window is re-MEASURED, and returning to a width that was
///          already laid out costs no per-block work at all.
/// Case 2 — appends / dirty heights / replacements: re-estimate the affected
///          suffix, patch virtual_y from the first touched index (O(1) for
///          the streaming tail).
/// Case 3 — nothing structural: totals and ranges served from cache, settle
///          any newly visible blocks.
///
/// **Cost of a width change (#412).** <c>IChatBlock.Measure</c> — the
/// expensive, exact call — was already bounded by the visible window. What was
/// NOT bounded is <c>IChatBlock.CheapEstimate</c>: a width change called it
/// once per block, so a resize cost O(transcript characters) in the render
/// frame to lay out a ~40-row viewport. Heights are now keyed by width and the
/// last <see cref="MaxRetainedWidths"/> layouts are kept, so returning to a
/// known width costs zero per-block work. Both prices are observable —
/// <see cref="EstimateCallsLastFrame"/> and <see cref="MeasureCallsLastFrame"/>
/// are the counters the #46 scale gates assert on. See
/// <see cref="PrepareLayout"/> for the bound.
/// </summary>
public sealed class TimelineLayoutCache
{
    private const int InitialSlots = 64;

    /// <summary>
    /// (#412) Alternate layout widths kept beside the active one. A width
    /// change used to discard every measured height, so flipping A→B→A paid a
    /// full re-estimate of the whole transcript. Two retained widths make the
    /// round trip free, which is the shape a real session produces (drag the
    /// splitter back, open a second pane, toggle a sidebar). The bound is
    /// fixed on purpose — memory per retained width is
    /// <c>Count × sizeof(Slot)</c> (12 bytes), so the ring is not a cache of
    /// unbounded width history. Measured cost of the miss path is documented
    /// on <see cref="PrepareLayout"/>.
    /// </summary>
    private const int MaxRetainedWidths = 2;

    /// <param name="width">
    /// Width the height was measured at — 0 for estimates, which carry no
    /// authority at any width. Stamped by <see cref="TimelineLayoutCache"/>
    /// because an exact height is only reusable by a layout running at the
    /// same width; without the stamp, re-adopting a retained width would also
    /// re-adopt measurements that have since gone stale.
    /// </param>
    private readonly struct Slot(int exactH, int estH, bool measured, int width = 0)
    {
        public int ExactH { get; } = exactH;
        public int EstH { get; } = estH;
        public bool Measured { get; } = measured;

        /// <summary>Width this exact height belongs to; 0 when not measured.</summary>
        public int MeasuredWidth { get; } = measured ? width : 0;

        public static Slot Estimated(int est) => new(-1, Math.Max(1, est), false);
        public static Slot ExactMeasured(int h, int width) => new(h, h, true, width);

        /// <summary>
        /// Placeholder for "no usable height here": the width seed once lived
        /// in this slot as <c>ReseededTo</c>, scaling the outgoing width's row
        /// count by <c>from/to</c> instead of asking the block. It made a
        /// first-time width change cost zero <see cref="IChatBlock.CheapEstimate"/>
        /// calls — and it was wrong. A wrapped line is not
        /// <c>ceil(totalChars/width)</c>: word breaks, collapse budgets and
        /// height-invariant blocks (images, tool headers) all break that ratio.
        /// Against a cold-cache oracle a 200→50 flip reported
        /// <c>TotalHeight</c> 10288 where the truth was 4998, so
        /// <c>EntryAtY</c> and the scrollbar extent were both wrong. Cheap was
        /// not worth wrong — the estimate pass is the honest price of a width
        /// this cache has never seen, and it is counted.
        /// </summary>
    }

    /// <summary>A layout width plus the heights measured/estimated for it.</summary>
    private readonly struct WidthSnapshot(int width, Slot[] slots)
    {
        public int Width { get; } = width;
        public Slot[] Slots { get; } = slots;
    }

    private IChatBlock[] _blocks = [];
    private Slot[] _slots = [];                // heights of the ACTIVE width
    private long[] _virtual = [0]; // _virtual[i] = top row of block i; [_count] = total height
    private int _count;

    private int _width = -1;
    private int _unmeasuredFrom;               // first index lacking any height info
    private int _dirtyFrom = int.MaxValue;     // first index whose cached height may be stale
    private int _measureCallsThisFrame;
    private int _estimateCallsThisFrame;

    // (#412) Width-keyed layout memory. `_slots` is the active width's array;
    // up to MaxRetainedWidths others sit in `_retained`, so a width already
    // laid out costs zero re-estimates when the viewport returns to it. Every
    // array is kept index-aligned with `_blocks` (see ResetSlotAt /
    // EvictFirst), which is what makes adopting one sound.
    private readonly WidthSnapshot[] _retained = new WidthSnapshot[MaxRetainedWidths];
    private int _retainedCount;

    // Scroll anchor: block IDENTITY + row within it, captured before rebuilds.
    // There is deliberately no pixel field (#416): the anchor is restored from
    // identity alone, so a stale Y can never be returned as a substitute.
    private IChatBlock? _anchorBlock;
    private int _anchorRow;

    public int Count => _count;

    public long TotalHeight => _virtual[_count];

    /// <summary>
    /// Largest legal timeline-space scroll offset for the given viewport height
    /// (CF-B-006 store bridge): <c>max(0, TotalHeight - viewportH)</c>. The host
    /// feeds this into <c>AppMsg.ScrollClamp</c> after layout so the store's
    /// <c>ScrollOffset</c> stays inside the freshly measured range. Pure and
    /// allocation-free; never mutates layout state. Delegates to
    /// <see cref="ScrollableViewport.MaxOffsetFor"/> so every widget shares
    /// one bounds formula.
    /// </summary>
    public long MaxScrollFor(int viewportH) => ScrollableViewport.MaxOffsetFor(TotalHeight, viewportH);

    /// <summary>
    /// Clamps a timeline-space scroll offset to <c>[0 .. MaxScrollFor(viewportH)]</c>.
    /// Same range the store enforces via <c>UiState.SetScroll</c>; kept here so the
    /// widget and the reducer can never disagree on the bounds formula.
    /// Delegates to <see cref="ScrollableViewport.ClampOffsetFor"/>.
    /// </summary>
    public long ClampScrollY(long scrollY, int viewportH) =>
        ScrollableViewport.ClampOffsetFor(scrollY, TotalHeight, viewportH);

    public IChatBlock BlockAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        return _blocks[index];
    }

    /// <summary>Measure() calls issued during the last <see cref="PrepareLayout"/>.</summary>
    public int MeasureCallsLastFrame => _measureCallsThisFrame;

    /// <summary>
    /// (#412) <c>CheapEstimate</c> calls issued during the last
    /// <see cref="PrepareLayout"/>. This is the per-item price of layout and
    /// the honest counterpart of <see cref="MeasureCallsLastFrame"/>: a
    /// "virtualized" timeline that re-estimates every block still charges the
    /// user for all N while showing K. A scroll frame through measured heights
    /// and a return to an already-laid-out width must both read 0.
    /// </summary>
    public int EstimateCallsLastFrame => _estimateCallsThisFrame;

    /// <summary>
    /// (#412) Widths whose heights are currently retained, active one first.
    /// Exposed for the width-keying test so the bound is asserted, not assumed.
    /// </summary>
    public int RetainedWidthCount => _retainedCount + (_width >= 0 ? 1 : 0);

    public void Append(IChatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        EnsureCapacity(_count + 1);
        _blocks[_count] = block;
        _count++;
        EnsureVirtualLength(_count + 1);

        // The new index has no height at ANY width, so every width-keyed
        // array gets a cleared slot — otherwise a later adopt of a retained
        // width would resurrect a stale row for a block appended after it.
        ResetSlotAt(_count - 1);

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

            // Every retained width shifts too: its slot i described _blocks[i]
            // before the drop and must describe it after, or adopting that
            // width later paints block i at block i+1's height.
            Array.Copy(_slots, 1, _slots, 0, _count - 1);
            foreach (ref var snap in _retained.AsSpan(0, _retainedCount))
            {
                var s = snap.Slots;
                Array.Copy(s, 1, s, 0, _count - 1);
            }
        }

        _count--;
        _blocks[_count] = null!;
        ResetSlotAt(_count);
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
            ClearMeasuredAt(i);
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

        var superseded = _blocks[index];
        _blocks[index] = block;
        ResetSlotAt(index);
        _unmeasuredFrom = Math.Min(_unmeasuredFrom, index);
        _dirtyFrom = Math.Min(_dirtyFrom, index);

        // #416: the replacement is the SAME message in the SAME slot, so an
        // anchor pointing at the superseded instance must follow it. Without
        // this, the streaming commit path (FinishStream -> Replace) orphans
        // the anchor, and the next width flip degrades it to row 0 instead of
        // holding the block the user was reading.
        if (ReferenceEquals(_anchorBlock, superseded))
        {
            _anchorBlock = block;
        }
    }

    /// <summary>
    /// Captures the viewport top as <b>identity</b> — (block reference, row
    /// within it) — so a width-change rebuild can restore the anchored CONTENT
    /// rather than the same pixel. The pixel is only ever an input here, used to
    /// resolve which block the viewport top lands in; it is never retained.
    /// </summary>
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
    }

    /// <summary>
    /// Runs the 3-case layout for the frame. After a full rebuild call
    /// <see cref="RestoreAnchor"/> to de-jump the viewport.
    ///
    /// <para><b>Cost of Case 1 (#412), stated so nobody assumes otherwise.</b>
    /// <c>Measure()</c> — the expensive, exact call — was already bounded by
    /// the visible window and still is. What was <i>not</i> bounded was
    /// <c>CheapEstimate()</c>: a width change called it once per block, so the
    /// price of a resize scaled with the transcript, not the viewport. The
    /// heights are now <b>keyed by width</b> and the last
    /// <see cref="MaxRetainedWidths"/> layouts are kept, so:
    /// <list type="bullet">
    /// <item><description><b>Width already laid out</b> — its heights are
    /// adopted verbatim: <c>EstimateCallsLastFrame == 0</c> and
    /// <c>MeasureCallsLastFrame == 0</c> unless a block appended or mutated
    /// since. A→B→A costs <i>nothing</i>, which is the round trip a real
    /// session produces (drag the splitter back, toggle a sidebar, focus a
    /// pane).</description></item>
    /// <item><description><b>Width never seen</b> (or one the ring has
    /// already evicted) — every height is invalidated and each block is asked
    /// once. This is <c>O(transcript characters)</c>, and it is counted, not
    /// hidden: <c>EstimateCallsLastFrame == Count</c>. Sustained resize-drag
    /// (a fresh width every frame) stays on this path, bounded by the ring.</description></item>
    /// </list>
    /// The alternative — scaling the previous width's row counts instead of
    /// re-estimating — was implemented, measured, and reverted: it made
    /// <see cref="TotalHeight"/> wrong by ~2× on a 200→50 flip, moving
    /// <see cref="EntryAtY"/> and the scrollbar with it. See <see cref="Slot"/>.</para>
    /// </summary>
    public LayoutOutcome PrepareLayout(int width, int viewportH, long scrollY)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportH);
        _measureCallsThisFrame = 0;
        _estimateCallsThisFrame = 0;

        // ── Case 1: width changed ───────────────────────────────────────────
        if (width != _width)
        {
            SeedForNewWidth(from: _width, to: width);
            _width = width;
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

    /// <summary>
    /// Post-rebuild scroll fix-up: keeps the anchored block at its row.
    ///
    /// <para><b>Degradation is specified, never guessed (#416).</b> When the
    /// anchored block is no longer resident — evicted by the ring, or replaced
    /// in place by a newer instance of the same message — the anchor degrades to
    /// the <b>nearest surviving position</b> (the top of the rebuilt timeline,
    /// row 0) rather than to the pixel offset captured before the rebuild. That
    /// offset belonged to the <i>previous</i> geometry: after a width flip every
    /// row height was re-estimated, so re-applying it lands the viewport on
    /// unrelated content, or past the end of a shrunken timeline. The old code
    /// returned that stale pixel and left the caller to clamp, which is why the
    /// eviction AC ("degrades in a specified way, not a silent jump") was
    /// unmeetable — the degradation was a coincidence, not a rule.</para>
    ///
    /// <para>Returning row 0 is the same answer for the two null cases that
    /// previously fell through here by luck (<c>PinAnchor</c> never called, and
    /// <c>Clear</c> just run), so this is behaviour-identical there and only
    /// changes the case that was wrong.</para>
    /// </summary>
    public long RestoreAnchor()
    {
        if (_anchorBlock is null || _count == 0)
        {
            return 0;
        }

        for (int i = 0; i < _count; i++)
        {
            if (ReferenceEquals(_blocks[i], _anchorBlock))
            {
                return _virtual[i] + _anchorRow;
            }
        }

        return 0; // anchored block is gone (evicted, or replaced in place)
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

    /// <summary>
    /// (#412) Prepares <see cref="_slots"/> for the width that was just
    /// assigned to <see cref="_width"/>, and parks the outgoing width's
    /// heights in the bounded retention ring.
    ///
    /// Three outcomes, all O(blocks) integer work and none of them a block
    /// call per transcript block:
    /// <list type="bullet">
    /// <item><description>The new width is in the ring → adopt that array as-is
    /// (its exact heights are valid again at this width) and retire the
    /// outgoing width in its place.</description></item>
    /// <item><description>Not in the ring → every height was wrapped at another
    /// width, so none is reusable: clear the array (recycled from the ring
    /// when full, so this does not allocate) and let
    /// <see cref="ComputeEstimates"/> ask each block once. Counted in
    /// <c>EstimateCallsLastFrame</c>.</description></item>
    /// <item><description>Cold cache (<paramref name="from"/> was unset) →
    /// nothing to park; same per-block pass.</description></item>
    /// </list>
    /// The ring is a fixed-size FIFO, so a sustained resize drag (a new width
    /// every frame) degrades to the estimate path rather than growing memory.
    /// </summary>
    private void SeedForNewWidth(int from, int to)
    {
        // Park the outgoing width, taking back its array when the ring is full
        // so a miss recycles storage instead of allocating.
        Slot[]? recycled = from >= 0 ? RetainOutgoing(from, _slots) : null;

        for (int i = 0; i < _retainedCount; i++)
        {
            if (_retained[i].Width == to)
            {
                // Cache hit: this width was laid out before, so its exact
                // heights are the right answer — no estimate, no re-measure.
                var adopted = _retained[i].Slots;
                for (int k = i; k < _retainedCount - 1; k++)
                {
                    _retained[k] = _retained[k + 1];
                }

                _retainedCount--;

                // The snapshot was parked when this width was last active. Adopt it as-is:
                // every slot in it — exact AND estimated — was produced for
                // THIS width, so all of it is the right answer.
                //
                // The current array must not be merged in. Its exact heights
                // were measured at `from` and its estimates computed for
                // `from`; carrying either across reintroduces exactly the
                // stale-width row count the width key exists to prevent
                // (measured: a 100→50→100 round trip reported TotalHeight 190
                // where a cold cache said 218).
                //
                // The only thing the current array knows that the snapshot
                // cannot is a measurement taken at `to` since the snapshot was
                // parked — impossible by construction: `to` was never the
                // active width while the snapshot existed, so nothing can
                // have been measured at `to` more recently.
                _slots = adopted;
                return;
            }
        }

        if (_count == 0)
        {
            return;
        }

        // Cache miss on a width this cache has never laid out. Recycle the
        // evicted array and wipe it: every height was wrapped at another
        // width, so none of them survives. ComputeEstimates then asks each
        // block once — O(transcript characters), counted in
        // EstimateCallsLastFrame. That is the honest price of an unseen width
        // and it is what the numbers in the PR body are measuring; the win
        // this slice buys is that the round trip A→B→A never pays it twice.
        var fresh = recycled ?? new Slot[Math.Max(_count, InitialSlots)];
        Array.Clear(fresh, 0, _count);
        _slots = fresh;
    }

    /// <summary>
    /// Parks <paramref name="slots"/> as the snapshot for width
    /// <paramref name="width"/> and returns the array the ring had to give up
    /// (null while it is still filling). FIFO by insertion order:
    /// <see cref="_retained"/>[0] is the oldest.
    /// </summary>
    private Slot[]? RetainOutgoing(int width, Slot[] slots)
    {
        if (_retainedCount == MaxRetainedWidths)
        {
            var evicted = _retained[0].Slots;
            for (int k = 1; k < _retainedCount; k++)
            {
                _retained[k - 1] = _retained[k];
            }

            _retained[_retainedCount - 1] = new WidthSnapshot(width, slots);
            return evicted;
        }

        _retained[_retainedCount++] = new WidthSnapshot(width, slots);
        return null;
    }

    /// <summary>
    /// Clears one index in the active and every retained array, so no width
    /// keeps a row count for a block that was just appended (no height yet),
    /// replaced (different block), or evicted (slot now free).
    ///
    /// The active array is grown by <see cref="EnsureCapacity"/> before every
    /// call; retained arrays are guaranteed to be at least as long as
    /// <see cref="_slots"/> by the same method, but the bound is still checked
    /// rather than assumed — a short array here would silently keep a stale
    /// height for a shifted block, which is a correctness bug, and the cost
    /// of the check is one compare on an event path.
    /// </summary>
    private void ResetSlotAt(int index)
    {
        _slots[index] = default;
        for (int w = 0; w < _retainedCount; w++)
        {
            var s = _retained[w].Slots;
            if (index >= s.Length)
            {
                Array.Resize(ref s, _slots.Length);
                _retained[w] = new WidthSnapshot(_retained[w].Width, s);
            }

            s[index] = default;
        }
    }

    /// <summary>
    /// Drops an exact measurement at one index across every width-keyed array
    /// — a mutated card invalidates the row count at all widths, not just the
    /// active one, or a retained width would re-adopt a height that no longer
    /// describes the block.
    /// </summary>
    private void ClearMeasuredAt(int index)
    {
        DemoteSlot(_slots, index);
        for (int w = 0; w < _retainedCount; w++)
        {
            DemoteSlot(_retained[w].Slots, index);
        }
    }

    private static void DemoteSlot(Slot[] slots, int index)
    {
        if (index < slots.Length && slots[index].Measured)
        {
            slots[index] = default;
        }
    }

    private void ComputeEstimates()
    {
        for (int i = _unmeasuredFrom; i < _count; i++)
        {
            ref var s = ref _slots[i];
            if (!s.Measured && s.EstH == 0)
            {
                _estimateCallsThisFrame++;
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
                UiStageCounters.CountBlockLayout(); // #409 layout stage — next to the existing measure tally
                s = m.IsExact ? Slot.ExactMeasured(m.MaxLines, _width) : Slot.Estimated(m.BestGuess);
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
        // Gate on BOTH arrays, not just _blocks: a width miss hands _slots a
        // right-sized array (see SeedForNewWidth), so _blocks can be long
        // while _slots is short. Keying the early-out on _blocks alone then
        // skipped the growth and the next Append wrote past the end.
        if (needed <= _blocks.Length && needed <= _slots.Length)
        {
            return;
        }

        int cap = Math.Max(InitialSlots, Math.Max(_blocks.Length, _slots.Length) * 2);
        while (cap < needed)
        {
            cap *= 2;
        }

        Array.Resize(ref _blocks, cap);
        Array.Resize(ref _slots, cap);

        // Retained width arrays stay index-aligned with _blocks (see the
        // invariant on _retained); grow them together or an adopted array
        // would be shorter than the block array.
        for (int w = 0; w < _retainedCount; w++)
        {
            var snapshot = _retained[w];
            var grown = new Slot[cap];
            Array.Copy(snapshot.Slots, grown, Math.Min(snapshot.Slots.Length, cap));
            _retained[w] = new WidthSnapshot(snapshot.Width, grown);
        }
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

    public void Clear()
    {
        _count = 0;
        Array.Clear(_blocks, 0, _blocks.Length);
        Array.Clear(_slots, 0, _slots.Length);
        _virtual[0] = 0;
        _width = -1;
        _unmeasuredFrom = 0;
        _dirtyFrom = int.MaxValue;
        _anchorBlock = null;
        _anchorRow = 0;

        // #412: drop the retained widths too. A new session's blocks are not
        // the evicted ones, so a surviving snapshot would re-adopt row counts
        // that describe blocks that no longer exist.
        //
        // Note: #416 removed the _anchorY field (the anchor is identity, never
        // a retained pixel), so there is nothing to reset here for it.
        _retainedCount = 0;
        Array.Clear(_retained);
    }
}
