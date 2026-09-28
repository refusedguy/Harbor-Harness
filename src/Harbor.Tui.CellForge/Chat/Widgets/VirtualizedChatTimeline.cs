using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// The virtualized chat feed (widgets §3.3): renders only the blocks that
/// intersect the viewport (lazygit viewport-only), follows the tail while the
/// user is pinned to it and unpins on any upward scroll. Storage, heights and
/// virtual geometry live in <see cref="TimelineLayoutCache"/>.
/// </summary>
public sealed class VirtualizedChatTimeline
{
    /// <summary>Upper bound on narrow (per-widget) damage rects reported per frame.</summary>
    public const int MaxFxDamage = 8;

    private readonly TimelineLayoutCache _cache = new();

    /// <summary>
    /// Windowed-render viewport (ENG8 #279, btea viewport pattern): owns the
    /// visible row slice (clamped offset + clipped rows) over the settled
    /// timeline height. Published from <see cref="ScrollY"/> on every layout /
    /// paint pass — <see cref="Paint"/> renders only this window, never the
    /// whole buffer. Same shape as <see cref="CommandPaletteView.Viewport"/>.
    /// </summary>
    private readonly ScrollableViewport _viewport = new();
    private readonly Dictionary<IChatBlock, long> _entranceStarts = new();
    private readonly Rect[] _fxDamage = new Rect[MaxFxDamage];
    private readonly GlowRegion[] _glowRegions = new GlowRegion[MaxFxDamage];

    /// <summary>
    /// Reusable draw-pass snapshot buffer (ENG12 #284): holds the visible
    /// window's block refs so an event-thread Append/Evict mid-draw cannot
    /// shift indices under the paint loop. Grown geometrically on demand —
    /// steady-state frames allocate nothing (moat-pinned).
    /// </summary>
    private IChatBlock[] _paintSnapshot = [];
    private int _fxDamageCount;
    private int _glowCount;
    private bool _broadDamage;

    /// <summary>
    /// Index of the block whose cached height went stale this frame, or -1 when
    /// nothing was marked. Dirty-rect invalidation (#465): the next paint turns
    /// this into ONE narrow rect — the block's top row down to the bottom of the
    /// timeline, since everything below a height change reflows — instead of
    /// forcing the host's viewport-wide full scan on every streaming frame.
    /// </summary>
    private int _pendingDirtyFrom = -1;

    /// <summary>Virtual top row of the last resolved dirty block. Folded into the
    /// next resolve with <c>min</c> so a block that moved up never leaves a band
    /// of stale rows behind; <see cref="long.MaxValue"/> until the first mark.</summary>
    private long _lastDirtyTop = long.MaxValue;

    /// <summary>Screen rect produced by <see cref="ResolveDirtyRect"/> for the
    /// frame. Reported at index 0 of the <see cref="ConsumeFrameDamage"/> output
    /// so hosts keep exactly one damage call site.</summary>
    private Rect _dirtyRect;

    private bool _dirtyRectValid;
    private long _lastScrollY = -1;
    private int _lastWidth = -1;
    private int _lastViewportH = -1;
    private bool _dirtyGeometry = true;
    private bool _entranceFx;
    private bool _smoothScroll;
    private bool _scrollAnimating;
    private double _visualScrollY;
    private double _scrollFrom;
    private long _scrollStartTick;
    private long _scrollTarget;

    /// <summary>Byte budget for resident history; oldest blocks evict first.</summary>
    public long BudgetBytes { get; set; } = TimelineRing.DefaultBudgetBytes;

    /// <summary>Running sum of resident block budgets — O(1) append bookkeeping
    /// instead of a per-append O(n) rescan; kept exact on append/replace/evict.</summary>
    private long _budgetUsed;

    /// <summary>
    /// Enables HDS v1 entrance motion for blocks appended while the feed is
    /// already visible: slide-up (<see cref="PanelFx.SlideMs" />) plus fade
    /// (<see cref="PanelFx.FadeMs" />). Blocks present at the first frame
    /// render settled, so initial screens stay pixel-stable. Off by default;
    /// hosts opt in via <see cref="EnableEntranceFx" />.
    /// </summary>
    public void EnableEntranceFx() => _entranceFx = true;

    /// <summary>
    /// Turns entrance motion back off — newly appended blocks render settled.
    /// Symmetric opt-out for hosts/tests that need phase-stable frames.
    /// </summary>
    public void DisableEntranceFx()
    {
        _entranceFx = false;
        _entranceStarts.Clear();
    }

    /// <summary>
    /// Enables smooth scrolling (HDS v1): user-initiated scroll deltas ease
    /// toward their target over the micro fade (ease-out, 150 ms) instead of
    /// jumping. Follow-tail motion and <see cref="ScrollToEnd" /> stay exact
    /// snaps — only viewport-relative movement animates. Off by default;
    /// hosts opt in via this method.
    /// </summary>
    public void EnableSmoothScroll() => _smoothScroll = true;

    /// <summary>
    /// Post-render glow feed (renderer-moat T3): when enabled, pending
    /// approval gates publish <see cref="GlowRegion"/>s every frame —
    /// INCLUDING pulse troughs (intensity 0) — so the host's effect pipeline
    /// can repaint the gate at zero strength and the glow never sticks to the
    /// terminal. Off by default; hosts that arm a <see cref="PostFxPipeline"/>
    /// opt in (byte-identical frames when off — golden contract).
    /// </summary>
    public bool EnablePostFx { get; set; }

    /// <summary>
    /// True when the last paint resolved a narrow dirty-rect
    /// (<see cref="MarkDirty"/>) that the host is about to receive at index 0
    /// of <see cref="ConsumeFrameDamage"/>. False on quiet frames, on frames
    /// that returned viewport-wide damage, and when the dirty block sits below
    /// the viewport. Read-only mirror of the ledger for hosts and tests that
    /// want to tell the two cases apart without inspecting rect geometry.
    ///
    /// <para>Read it BEFORE <see cref="ConsumeFrameDamage"/>: consuming hands
    /// the ledger to the host and clears it, so a later read reports false for
    /// every frame — including the ones that did produce a rect. Hosts that
    /// learn what to do from the count alone do not need it.</para>
    /// </summary>
    public bool HasDirtyRect => _dirtyRectValid;

    public int Count => _cache.Count;

    public long TotalHeight => _cache.TotalHeight;

    /// <summary>Top row of the viewport in virtual space.</summary>
    public long ScrollY { get; private set; }

    /// <summary>True while stuck to the bottom (default).</summary>
    public bool FollowTail { get; private set; } = true;

    /// <summary>
    /// The visible row window (ENG8 #279): synced from <see cref="ScrollY"/>
    /// on every <see cref="PrepareFrame"/> / <see cref="Paint"/> pass.
    /// <c>Offset</c> is the top row in timeline space; <c>VisibleSlice</c> is
    /// the clipped <c>[Offset .. min(Total, Offset + ViewportH))</c> rows the
    /// next paint renders. Same shape as
    /// <see cref="CommandPaletteView.Viewport"/>.
    /// </summary>
    public ScrollableViewport Viewport => _viewport;

    /// <summary>
    /// Panel chrome — inter-message separators in the feed: when true, message
    /// blocks with a trailing gap row (user / assistant bubbles) paint a thin
    /// dim separator line in it instead of a blank row. False by default, so
    /// existing timeline goldens stay byte-identical; the interactive host
    /// (CellForge REPL) enables it. Threading: set before/without concurrent
    /// paints, like the other render flags.
    /// </summary>
    public bool ShowSeparators { get; set; }

    /// <summary>Frame tick handed to block painters.</summary>
    public long CurrentTick { get; set; }

    private bool _hasPaintedFrame;

    public IChatBlock BlockAt(int index) => _cache.BlockAt(index);

    public void Append(IChatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        _cache.Append(block);
        MarkEntrance(block);
        _budgetUsed += Math.Max(0, block.BudgetBytes);
        _dirtyGeometry = true;
        _broadDamage = true;

        EvictOverBudget();
        MarkLastDirty();
    }

    /// <summary>Amortized eviction: triggered only when the running total
    /// crosses the budget, then evicts down to the 75 % low-water mark in one
    /// pass — streaming pays the pass once per ~¼ budget of new bytes instead
    /// of rescanning and evicting on every append (O(n²) during token storms).</summary>
    private void EvictOverBudget()
    {
        long budget = BudgetBytes;
        if (budget <= 0 || _budgetUsed <= budget)
        {
            return;
        }

        long lowWater = budget - (budget >> 2);
        while (_cache.Count > 1 && _budgetUsed > lowWater)
        {
            var evicted = _cache.BlockAt(0);
            _budgetUsed -= Math.Min(_budgetUsed, Math.Max(0, evicted.BudgetBytes));
            _ = _cache.EvictFirst();
            _ = _entranceStarts.Remove(evicted);
            _dirtyGeometry = true;
            _broadDamage = true;
        }
    }

    /// <summary>Swaps the live-stream placeholder for its committed form.</summary>
    public void ReplaceLast(IChatBlock block)
    {
        if (_cache.Count == 0)
        {
            Append(block);
            return;
        }

        var old = _cache.BlockAt(_cache.Count - 1);
        _budgetUsed += Math.Max(0, block.BudgetBytes) - Math.Max(0, old.BudgetBytes);
        _cache.Replace(_cache.Count - 1, block);
        _dirtyGeometry = true;
        _broadDamage = true;
        MarkLastDirty();
    }

    /// <summary>
    /// Replaces a specific block instance in place (the stream slot may sit
    /// below newer tool cards). No-op when the block is gone.
    /// </summary>
    public void Replace(IChatBlock existing, IChatBlock replacement)
    {
        ArgumentNullException.ThrowIfNull(existing);

        for (int i = 0; i < _cache.Count; i++)
        {
            if (ReferenceEquals(_cache.BlockAt(i), existing))
            {
                _budgetUsed += Math.Max(0, replacement.BudgetBytes) - Math.Max(0, existing.BudgetBytes);
                _cache.Replace(i, replacement);
                _dirtyGeometry = true;
                _broadDamage = true;
                _cache.MarkHeightsDirty(i);
                return;
            }
        }
    }

    /// <summary>Unknown card mutated in place — treat the whole frame as
    /// viewport-wide (partial-scan contract). This is the hook mutating
    /// components use when the changed block is NOT the streaming tail:
    /// approval gates and tool cards settle below newer blocks, so their
    /// rects are not the tail's suffix and only a full scan is safe.
    ///
    /// The streaming path must not use this — it marked every stream frame
    /// viewport-wide and cost a full scan per frame (#465). Use
    /// <see cref="MarkDirty"/> with the block that actually changed.</summary>
    public void MarkLastDirty()
    {
        _cache.MarkHeightsDirty(Math.Max(0, _cache.Count - 1));
        _broadDamage = true;
    }

    /// <summary>Dirty-rect invalidation for one resident block (#465): its
    /// cached height is stale, so the next frame damages the single rect from
    /// the block's top row down to the bottom of the timeline — everything
    /// below a height change reflows, and nothing above it can move.
    ///
    /// A no-op for a block that is no longer resident (evicted from the ring
    /// means nothing of it is painted any more). Callers must still mark
    /// viewport-wide damage for anything they cannot name a block for, and the
    /// frame still degrades to a full scan on its own whenever the viewport
    /// genuinely moved — scroll shift, width change, append, rewrap.</summary>
    public void MarkDirty(IChatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        int index = IndexOf(block);
        if (index < 0)
        {
            return;
        }

        _cache.MarkHeightsDirty(index);
        _pendingDirtyFrom = _pendingDirtyFrom < 0 ? index : Math.Min(_pendingDirtyFrom, index);
    }

    /// <summary>Index of a resident block, or -1 when it is not in the ring.
    /// Scans from the tail: the streaming block is the last entry, so the hot
    /// path costs one reference compare (a forward scan would be O(blocks) on
    /// every stream frame — the exact class of waste #465 removes).</summary>
    private int IndexOf(IChatBlock block)
    {
        for (int i = _cache.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_cache.BlockAt(i), block))
            {
                return i;
            }
        }

        return -1;
    }

    public void Clear()
    {
        _cache.Clear();
        _entranceStarts.Clear();
        _budgetUsed = 0;
        _viewport.SetTotal(0); // offset re-clamps to 0; height re-syncs on the next frame
        _dirtyGeometry = true;
        _broadDamage = true;
        _pendingDirtyFrom = -1;
        _lastDirtyTop = long.MaxValue;
        _dirtyRectValid = false;
        ScrollY = 0;
        FollowTail = true;
        _visualScrollY = 0;
        _scrollAnimating = false;
        _lastScrollY = -1;
        _lastViewportH = -1;
    }

    public void ScrollUp(int lines) => ScrollBy(-lines);

    public void ScrollDown(int lines) => ScrollBy(lines);

    public void ScrollBy(int lines)
    {
        if (lines < 0)
        {
            FollowTail = false;
        }

        long target = ScrollY + lines;
        long maxScroll = TotalHeightAfter(_lastViewportH);
        target = Math.Clamp(target, 0, maxScroll);

        long fromVisual = _scrollAnimating ? (long)Math.Round(_visualScrollY) : ScrollY;
        SetScrollY(target);
        BeginScrollAnimation(fromVisual);

        if (lines > 0 && target >= maxScroll)
        {
            FollowTail = true;
        }
    }

    public void PageUp(int viewportHeight) => ScrollBy(-Math.Max(1, viewportHeight - 1));

    public void PageDown(int viewportHeight) => ScrollBy(Math.Max(1, viewportHeight - 1));

    public void ScrollToTop()
    {
        FollowTail = false;
        SnapScroll(0);
    }

    /// <summary>Snaps to the bottom and re-engages follow mode.</summary>
    public void ScrollToEnd(int viewportHeight)
    {
        FollowTail = true;
        SnapScroll(TotalHeightAfter(viewportHeight));
    }

    // ── Store-driven scroll (CF-B-006 + CF-C-002/C-003) ────────────────────
    // UiState (Harbor.Ui.Framework.State) is the single source of truth for scroll
    // position: ScrollOffset (0 = pinned to the live tail, grows toward the top),
    // ViewportLines (visible history rows) and TotalLines (wrapped transcript rows).
    // These helpers only *build* UiMsg values for the host to dispatch via
    // UiStore.Dispatch and *read* UiState snapshots — dispatch stays with the host,
    // so the widget keeps no second scroll authority and the reducer stays pure.
    // Tail-follow is derived, never stored twice: ScrollOffset == 0 means pinned.
    // NOTE: CellForgeViewport has no Refresh/PrepareLayout methods (only
    // RefreshFromConsole/Resize/SetViewportLines/Apply) — layout runs through
    // TimelineLayoutCache.PrepareLayout via PrepareFrame below; the viewport
    // object itself is only read by the host, never mutated here.

    /// <summary>PageUp key → store page-up scroll (reducer clamps via SetScroll).</summary>
    public static UiMsg PageUpMsg() => new UiMsg.KeyInput(ChatAction.ScrollUpPage, new UiKey(UiKeyCode.PageUp));

    /// <summary>PageDown key → store page-down scroll (reducer clamps via SetScroll).</summary>
    public static UiMsg PageDownMsg() => new UiMsg.KeyInput(ChatAction.ScrollDownPage, new UiKey(UiKeyCode.PageDown));

    /// <summary>Up-arrow key → store single-line scroll up.</summary>
    public static UiMsg LineUpMsg() => new UiMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up));

    /// <summary>Down-arrow key → store single-line scroll down.</summary>
    public static UiMsg LineDownMsg() => new UiMsg.KeyInput(ChatAction.ScrollDownLine, new UiKey(UiKeyCode.Down));

    /// <summary>Home key → store jump to the oldest row (offset = max).</summary>
    public static UiMsg ScrollTopMsg() => new UiMsg.KeyInput(ChatAction.ScrollTop, new UiKey(UiKeyCode.Home));

    /// <summary>End key → store pin to the live tail (offset = 0).</summary>
    public static UiMsg ScrollBottomMsg() => new UiMsg.KeyInput(ChatAction.ScrollBottom, new UiKey(UiKeyCode.End));

    /// <summary>Pin to the live tail (offset = 0); the reducer also sets WasRunning.</summary>
    public static UiMsg ResetToTailMsg() => new UiMsg.ScrollResetToTail();

    /// <summary>
    /// Maps a mouse-wheel tick to the store scroll message. Positive
    /// <paramref name="delta"/> = wheel up (the <c>IPointerTarget</c> contract) →
    /// <c>ScrollUpLine</c>; negative → <c>ScrollDownLine</c>; zero → a
    /// <c>ChatAction.None</c> no-op. Line (not page) granularity: the reducer
    /// treats both identically (both clamp via <c>SetScroll</c>), and a full page
    /// per wheel tick is too coarse — hosts that want page steps dispatch
    /// <see cref="PageUpMsg"/> / <see cref="PageDownMsg"/> (possibly several line
    /// messages per tick for acceleration).
    /// </summary>
    public static UiMsg WheelMsg(int delta) =>
        delta > 0 ? LineUpMsg() : delta < 0 ? LineDownMsg() : new UiMsg.KeyInput(ChatAction.None, UiKey.Unknown);

    /// <summary>
    /// Mirrors a store snapshot into <see cref="ScrollY"/> / <see cref="FollowTail"/>
    /// and runs layout. Viewport height precedence: explicit
    /// <paramref name="viewportH"/> when positive, else
    /// <c>state.ViewportLines</c>. <c>state.TotalLines</c> is informational only —
    /// the authoritative total is the cache's <see cref="TotalHeight"/>, reported
    /// back to the store via <see cref="MeasureMsgs"/> (geometry flows
    /// timeline → store, never the reverse). Store offset maps to timeline space
    /// as <c>ScrollY = max - offset</c> (same convention as
    /// <c>CellForgeViewport.FirstVisibleRow</c>); a zero offset re-pins the tail.
    /// Post-layout the view is re-clamped to the freshly measured range without
    /// re-pinning, so a growing streaming tail cannot yank an unpinned view.
    /// </summary>
    public LayoutOutcome ApplyStoreState(UiState state, int width, int viewportH)
    {
        ArgumentNullException.ThrowIfNull(state);
        int viewH = viewportH > 0 ? viewportH : Math.Max(0, state.ViewportLines);
        FollowTail = state.ScrollOffset <= 0;
        if (!FollowTail)
        {
            // Pre-layout snap on the (possibly stale) range keeps the measure
            // window near the target; the authoritative snap below re-asserts
            // the store offset against the freshly settled total.
            SnapScroll(_cache.ClampScrollY(ScrollY, viewH));
        }

        var outcome = PrepareFrame(width, viewH);
        if (!FollowTail)
        {
            // Fresh max: TotalHeight settles only inside PrepareLayout
            // (post-Append _virtual[_count] is stale until patched), so the
            // store-driven position is mapped here. A FullRebuild anchor
            // restore is intentionally overridden: the store is the source
            // of truth; clamping (never re-pinning) keeps a growing
            // streaming tail from yanking an unpinned view.
            long max = _cache.MaxScrollFor(viewH);
            SnapScroll(Math.Clamp(max - (long)state.ScrollOffset, 0, max));
        }

        return outcome;
    }

    /// <summary>
    /// Builds the geometry messages the host dispatches after layout so the store
    /// tracks the measured viewport (resize path, CF-C-003): <c>Viewport</c> with
    /// the visible height, <c>HistoryMeasured</c> with the settled total
    /// (clamped to <c>int.MaxValue</c> — <c>UiState</c> totals are <c>int</c>),
    /// then <c>ScrollClamp</c> with the measured maximum. Order matters: the
    /// reducer's <c>Viewport</c>/<c>HistoryMeasured</c> arms do not clamp, so the
    /// host must always dispatch the trailing <c>ScrollClamp</c> (a shrunken
    /// viewport otherwise leaves a stale out-of-range offset).
    /// </summary>
    public UiMsg[] MeasureMsgs(int viewportH)
    {
        int viewH = Math.Max(0, viewportH);
        int total = (int)Math.Min(TotalHeight, int.MaxValue);
        int max = (int)Math.Min(_cache.MaxScrollFor(viewH), int.MaxValue);
        return new UiMsg[] { new UiMsg.Viewport(viewH), new UiMsg.HistoryMeasured(total), new UiMsg.ScrollClamp(max) };
    }

    /// <summary>Largest legal scroll offset for the settled total, routed
    /// through the owned <see cref="Viewport"/> so every scroll path shares
    /// the single <c>max(0, total - viewportH)</c> bounds formula.</summary>
    private long TotalHeightAfter(int viewportH)
    {
        _viewport.Configure(TotalHeight, viewportH);
        return _viewport.MaxOffset;
    }

    private void SetScrollY(long y)
    {
        _viewport.Configure(TotalHeight, Math.Max(0, _lastViewportH));
        ScrollY = _viewport.SetOffset(y);
        if (!_scrollAnimating)
        {
            _visualScrollY = ScrollY;
        }
    }

    /// <summary>Instant reposition — cancels any in-flight scroll animation.</summary>
    private void SnapScroll(long y)
    {
        _scrollAnimating = false;
        _visualScrollY = Math.Max(0, y);
        ScrollY = Math.Max(0, y);
    }

    /// <summary>
    /// Starts (or retargets) the eased scroll toward the current
    /// <see cref="ScrollY" /> from <paramref name="fromVisual" /> — the
    /// on-screen offset captured before the target moved — so consecutive
    /// wheel events glide instead of restarting or jumping.
    /// </summary>
    private void BeginScrollAnimation(long fromVisual)
    {
        if (!_smoothScroll || FollowTail || ScrollY == fromVisual)
        {
            return;
        }

        _scrollFrom = fromVisual;
        _visualScrollY = fromVisual;
        _scrollTarget = ScrollY;
        _scrollStartTick = CurrentTick;
        _scrollAnimating = true;
    }

    /// <summary>Scroll offset the next paint should use (animated value while easing).</summary>
    public long EffectiveScrollY => _scrollAnimating ? (long)Math.Round(_visualScrollY) : ScrollY;

    /// <summary>
    /// Registers an entrance start for eligible blocks appended after the
    /// first painted frame. Pre-first-frame appends (initial populate) and
    /// stream continuations render settled — no motion on cold screens.
    /// </summary>
    private void MarkEntrance(IChatBlock block)
    {
        if (!_entranceFx || !_hasPaintedFrame || block.IsStreamContinuation)
        {
            return;
        }

        _entranceStarts[block] = CurrentTick;
        if (block is ApprovalGateView gate && gate.IsPending)
        {
            gate.BeginWarnPulse(CurrentTick);
        }
    }

    /// <summary>Runs layout for this frame; resolves follow-tail and anchors.
    /// Any scroll shift, rewrap or full rebuild flags viewport-wide damage —
    /// partial-scan hints must never miss content that moved.</summary>
    public LayoutOutcome PrepareFrame(int width, int viewportH)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportH);

        _lastViewportH = viewportH;

        if (_lastWidth != width && _cache.Count > 0)
        {
            _broadDamage = true;
            _cache.PinAnchor(ScrollY);
        }

        if (FollowTail)
        {
            ScrollY = TotalHeightAfter(viewportH);
        }
        else
        {
            long maxScroll = TotalHeightAfter(viewportH);
            if (ScrollY > maxScroll)
            {
                ScrollY = maxScroll;
                _visualScrollY = maxScroll;
                _scrollAnimating = false;
            }
        }

        var outcome = _cache.PrepareLayout(width, viewportH, ScrollY);
        _lastWidth = width;
        _dirtyGeometry = false;

        if (outcome == LayoutOutcome.FullRebuild && _cache.Count > 0)
        {
            _broadDamage = true;
            ScrollY = Math.Max(0, _cache.RestoreAnchor());
        }

        if (FollowTail)
        {
            ScrollY = TotalHeightAfter(viewportH);
            if (_scrollAnimating)
            {
                _scrollAnimating = false;
                _visualScrollY = ScrollY;
            }
        }

        if (_scrollAnimating)
        {
            double t = Math.Clamp((CurrentTick - _scrollStartTick) / (double)PanelFx.FadeFrames, 0.0, 1.0);
            _visualScrollY = _scrollFrom + ((_scrollTarget - _scrollFrom) * PanelFx.EaseOut(t));
            if (t >= 1.0)
            {
                _scrollAnimating = false;
                _visualScrollY = _scrollTarget;
            }
        }

        // Any visible scroll movement re-homes every row — viewport-wide.
        if (EffectiveScrollY != _lastScrollY)
        {
            _broadDamage = true;
            _lastScrollY = EffectiveScrollY;
        }

        // ENG8 #279: publish the settled window (offset + clipped rows) so
        // hosts/tests can read the exact slice the next paint renders.
        // Publish-only: ScrollY stays the logical authority (anchor restores
        // may legitimately sit past the settled max for a frame).
        _viewport.Configure(TotalHeight, viewportH);
        _viewport.SetOffset(ScrollY);

        return outcome;
    }

    /// <summary>
    /// Paints only the visible range of blocks into <paramref name="rect"/>.
    /// Cells outside the rect are never touched. With entrance FX enabled,
    /// freshly appended blocks slide up (<see cref="PanelFx.SlideMaxRows" />)
    /// and fade in over the HDS motion durations.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        // Any executed paint pass counts as a "visible" frame — appends made
        // afterwards become eligible for entrance motion.
        _hasPaintedFrame = true;

        if (_dirtyGeometry || _cache.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        // Erase the previous frame's timeline content first: scroll glides
        // and appends shift blocks row-by-row, and without a rect-level blank
        // every vacated row keeps its stale cells as ghost trails (same class
        // of bug as the composer's SetText("") no-op erase).
        buffer.Fill(rect, Cell.Blank);

        // ENG8 #279 (btea viewport pattern): the paint window comes from the
        // viewport — offset clamped to the settled total, rows clipped to the
        // rect. Long timelines render only this slice, never the whole
        // buffer. In-range frames are a no-op clamp (top == EffectiveScrollY),
        // so settled output is byte-identical; a stale offset (evict /
        // anchor-restore past the settled max) paints the last window instead
        // of an empty/shifted one.
        _viewport.Configure(TotalHeight, rect.Height);
        long top = _viewport.SetOffset(EffectiveScrollY);
        ResolveDirtyRect(rect, top);
        var (first, last) = _cache.VisibleRange(top, rect.Height);
        // ENG12 #284 (TGui snapshot pattern): event-thread Append/Evict
        // shifts cache indices mid-draw — snapshot the visible window's refs
        // so BlockAt can never throw past the end. The buffer is reused
        // across frames (grown geometrically): steady-state paints allocate
        // nothing. Geometry below is still read per index with a guard; a
        // torn frame self-corrects next pass.
        int lo = Math.Max(first, 0);
        int hi = Math.Min(last, _cache.Count - 1);
        int window = Math.Max(0, hi - lo + 1);
        if (_paintSnapshot.Length < window)
        {
            _paintSnapshot = new IChatBlock[window];
        }
        var snapshot = _paintSnapshot;
        for (int k = 0; k < window; k++)
        {
            snapshot[k] = _cache.BlockAt(lo + k);
        }
        for (int i = first; i <= last && i <= hi; i++)
        {
            if (i < lo)
            {
                continue;
            }
            if (i >= _cache.Count)
            {
                break;
            }
            long blockTop = _cache.BlockTop(i);
            long relTop = blockTop - top;
            int screenY = rect.Y + (int)Math.Max(0, relTop);
            int skipRows = relTop < 0 ? -(int)relTop : 0;
            int h = _cache.EffectiveHeight(i);
            int visibleRows = h - skipRows;
            int clipped = Math.Min(visibleRows, rect.Bottom - screenY);
            if (clipped <= 0)
            {
                continue;
            }

            double alpha = 1.0;
            var block = snapshot[i - lo];
            bool entrance = _entranceStarts.TryGetValue(block, out long startTick);
            bool animating = entrance;
            if (animating)
            {
                alpha = PanelFx.Progress(startTick, CurrentTick, PanelFx.FadeFrames);
                if (alpha >= 1.0)
                {
                    _ = _entranceStarts.Remove(block);
                    animating = false;
                }
            }

            int paintY = screenY;
            int paintH = clipped;
            if (animating && alpha < 1.0)
            {
                double slideP = PanelFx.Progress(startTick, CurrentTick, PanelFx.SlideFrames);
                int offset = (int)Math.Round((1.0 - slideP) * PanelFx.SlideMaxRows); // slides up into place
                if (offset > 0)
                {
                    paintY += Math.Min(offset, rect.Bottom - paintY - 1);
                    if (paintY > rect.Y + rect.Height)
                    {
                        continue; // fully below the clip this frame
                    }

                    paintH = clipped - (paintY - screenY);
                }
            }

            var ctx = new BlockPaintContext(buffer, new Rect(rect.X, paintY, rect.Width, Math.Max(1, paintH)), CurrentTick, skipRows, ShowSeparators);
            block.Paint(ctx);

            // Narrow (per-widget) damage bookkeeping: entrance fades and
            // pending approval-gate pulses are the only blocks whose cells
            // mutate between user events — everything else repaints identically.
            // The settle frame (fade ends this frame) counts too: styles jump
            // from the faded blend to the final ones exactly once.
            var paintedRect = new Rect(rect.X, paintY, rect.Width, Math.Max(1, paintH));
            bool fading = animating && alpha < 1.0;
            bool fx = fading || (entrance && !animating);
            if (!fx && block is ApprovalGateView { IsPending: true } gate && gate.PulseBirthTick >= 0)
            {
                double pulse = PanelFx.WarnPulse(gate.PulseBirthTick, CurrentTick);
                fx = pulse > 0 || EnablePostFx; // post-fx: troughs must repaint too (glow convergence)
                if (EnablePostFx && _glowCount < MaxFxDamage)
                {
                    // Accent = the exact header tone painted this frame —
                    // captured from the shared WarnTone source, never guessed.
                    _glowRegions[_glowCount++] = new GlowRegion(
                        paintedRect,
                        PanelFx.WarnTone(gate.PulseBirthTick, CurrentTick).Fg,
                        pulse);
                }
            }

            if (fx && _fxDamageCount < MaxFxDamage)
            {
                // Slide corridor (partial-scan contract): during entrance a
                // block paints up to SlideMaxRows BELOW its final slot, so
                // rows vacated since the previous frame sit under the painted
                // rect — extend the damage down or ghosts survive the scan.
                _fxDamage[_fxDamageCount++] = fading
                    ? new Rect(paintedRect.X, paintedRect.Y, paintedRect.Width, paintedRect.Height + PanelFx.SlideMaxRows)
                    : paintedRect;
            }
            else if (fx)
            {
                _broadDamage = true; // ledger overflow — don't drop damage silently
            }

            if (fading)
            {
                PanelFx.BlendRegion(buffer, paintedRect, alpha);
            }
        }
    }

    /// <summary>
    /// Turns the frame's narrow dirty mark into the one rect that can differ:
    /// the dirty block's top row through the bottom of the timeline rect (every
    /// row below a height change reflows, none above it can move). The
    /// previous frame's top row is folded in with <c>min</c> so a block that
    /// shifted never leaves a band of stale rows behind.
    ///
    /// An empty result means the dirty block sits entirely below the viewport —
    /// the frame changed nothing visible, and the host may take the hinted path
    /// with no timeline damage at all. Frames that genuinely moved keep the
    /// viewport-wide flag and return before this rect is ever consulted.</summary>
    private void ResolveDirtyRect(Rect rect, long top)
    {
        _dirtyRectValid = false;

        int index = _pendingDirtyFrom;
        _pendingDirtyFrom = -1;
        if (index < 0 || _cache.Count == 0 || rect.Height <= 0)
        {
            return;
        }

        // Eviction/append can move the index between mark and paint; clamp
        // instead of trusting it, and re-scan for the block when it fell out.
        int count = _cache.Count;
        if (index >= count)
        {
            index = count - 1;
        }

        long blockTop = _cache.BlockTop(index);
        long from = Math.Min(blockTop, _lastDirtyTop);
        _lastDirtyTop = blockTop;

        int y = rect.Y + (int)Math.Clamp(from - top, 0, rect.Height);
        int height = rect.Bottom - y;
        if (height <= 0)
        {
            return; // dirty block is below the viewport — nothing visible moved
        }

        _dirtyRect = new Rect(rect.X, y, rect.Width, height);
        _dirtyRectValid = true;
    }

    /// <summary>
    /// Hands the frame's damage to the host and resets the ledger. Returns
    /// true when damage is viewport-wide (appends, scroll, rewrap, unknown-card
    /// mutation) — the host must then run a plain full scan. When false, the
    /// <paramref name="fxOut"/> span receives the narrow rects that may have
    /// changed: index 0 is the dirty-rect from <see cref="MarkDirty"/> when
    /// <see cref="HasDirtyRect"/> holds, then the per-widget animation rects
    /// (empty = the feed was quiet this frame); everything outside those rects
    /// is known-identical. A span too small to carry the whole ledger degrades
    /// to the full scan rather than dropping damage silently.
    /// </summary>
    public bool ConsumeFrameDamage(Span<Rect> fxOut, out int fxCount)
    {
        bool broad = _broadDamage;
        int dirty = _dirtyRectValid ? 1 : 0;
        int fx = _fxDamageCount;

        // The dirty rect rides at index 0 and is never truncated; if the caller's
        // span cannot carry it AND every animation rect, fall back to a full scan.
        if (!broad && dirty + fx > fxOut.Length)
        {
            broad = true;
        }

        if (broad)
        {
            fxCount = 0;
        }
        else
        {
            fxCount = dirty + fx;
            int at = 0;
            if (dirty == 1)
            {
                fxOut[0] = _dirtyRect;
                at = 1;
            }

            for (int i = 0; i < fx; i++)
            {
                fxOut[at + i] = _fxDamage[i];
            }
        }

        _broadDamage = false;
        _fxDamageCount = 0;
        _dirtyRectValid = false;
        return broad;
    }

    /// <summary>
    /// Hands the frame's glow sources to the host and resets the ledger
    /// (renderer-moat T3): regions for pending approval gates this frame —
    /// empty when the feed is quiet, the post-fx feed is disabled, or the
    /// ledger overflowed (overflow only drops narrow rects, never damage).
    /// </summary>
    public int ConsumeGlowRegions(Span<GlowRegion> regions)
    {
        int count = Math.Min(_glowCount, regions.Length);
        for (int i = 0; i < count; i++)
        {
            regions[i] = _glowRegions[i];
        }

        _glowCount = 0;
        return count;
    }

    public (int First, int Last) VisibleRange(int viewportH) => _cache.VisibleRange(ScrollY, viewportH);
}
