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
    private readonly Dictionary<IChatBlock, long> _entranceStarts = new();
    private readonly Rect[] _fxDamage = new Rect[MaxFxDamage];
    private readonly GlowRegion[] _glowRegions = new GlowRegion[MaxFxDamage];
    private int _fxDamageCount;
    private int _glowCount;
    private bool _broadDamage;
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

    public int Count => _cache.Count;

    public long TotalHeight => _cache.TotalHeight;

    /// <summary>Top row of the viewport in virtual space.</summary>
    public long ScrollY { get; private set; }

    /// <summary>True while stuck to the bottom (default).</summary>
    public bool FollowTail { get; private set; } = true;

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

    /// <summary>Streaming tail grew — last block's cached height is stale.
    /// Height changes can reflow every row below the block, so the next
    /// frame's damage is treated as viewport-wide (partial-scan contract).</summary>
    public void MarkLastDirty()
    {
        _cache.MarkHeightsDirty(Math.Max(0, _cache.Count - 1));
        _broadDamage = true;
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
        long maxScroll = _lastViewportH > 0 ? TotalHeightAfter(_lastViewportH) : Math.Max(0, TotalHeight);
        target = Math.Clamp(target, 0, maxScroll);

        long fromVisual = _scrollAnimating ? (long)Math.Round(_visualScrollY) : ScrollY;
        SetScrollY(target);
        BeginScrollAnimation(fromVisual);
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

    private long TotalHeightAfter(int viewportH) => Math.Max(0, TotalHeight - viewportH);

    private void SetScrollY(long y)
    {
        long maxScroll = _lastViewportH > 0 ? TotalHeightAfter(_lastViewportH) : Math.Max(0, TotalHeight);
        ScrollY = Math.Clamp(y, 0, maxScroll);
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
            ScrollY = Math.Max(0, TotalHeight - viewportH);
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

        var (first, last) = _cache.VisibleRange(EffectiveScrollY, rect.Height);
        for (int i = first; i <= last; i++)
        {
            long blockTop = _cache.BlockTop(i);
            long relTop = blockTop - EffectiveScrollY;
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
            var block = _cache.BlockAt(i);
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

            var ctx = new BlockPaintContext(buffer, new Rect(rect.X, paintY, rect.Width, Math.Max(1, paintH)), CurrentTick);
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
    /// Hands the frame's damage to the host and resets the ledger. Returns
    /// true when damage is viewport-wide (appends, scroll, rewrap, streaming
    /// reflow) — the host must then run a plain full scan. When false, the
    /// <paramref name="fxOut"/> span receives the narrow per-widget rects that
    /// may have changed (empty = the feed was quiet this frame); everything
    /// outside those rects is known-identical.
    /// </summary>
    public bool ConsumeFrameDamage(Span<Rect> fxOut, out int fxCount)
    {
        bool broad = _broadDamage;
        fxCount = broad ? 0 : Math.Min(_fxDamageCount, fxOut.Length);
        if (!broad)
        {
            for (int i = 0; i < fxCount; i++)
            {
                fxOut[i] = _fxDamage[i];
            }
        }

        _broadDamage = false;
        _fxDamageCount = 0;
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

/// <summary>Layout-tree leaf hosting the chat feed (vertical split over the composer).</summary>
public sealed class ChatTimelinePanel : Rendering.Panel
{
    public ChatTimelinePanel(string id, int minWidth, int minHeight, int priority = 10)
        : base(id, new Size(minWidth, minHeight), priority)
    {
    }

    public VirtualizedChatTimeline Timeline { get; } = new();

    public override void Paint(ScreenBuffer buffer)
    {
        Timeline.CurrentTick++;
        Timeline.Paint(buffer, Rect);
    }
}

/// <summary>Live streaming thinking block: accumulates reasoning text and
/// re-renders it with dim+italic styling on every layout pass.</summary>
public sealed class StreamingThinkingBlock : IChatBlock
{
    private readonly StringBuilder _text = new();
    private int _width = -1;
    private string[] _lines = [];

    public string Kind => "thinking";

    public bool IsStreamContinuation => true;

    public int BudgetBytes => 48 + (_text.Length * 2);

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        _text.Append(delta);
        _width = -1;
    }

    public BlockMeasure Measure(int width)
    {
        EnsureWrapped(width);
        return BlockMeasure.Exact(Math.Max(1, _lines.Length));
    }

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.ToString(), Math.Max(1, width));

    public void Paint(in BlockPaintContext ctx)
    {
        EnsureWrapped(ctx.Rect.Width);
        var buffer = ctx.Buffer;
        int rows = ctx.Rect.Height;
        for (int i = 0; i < _lines.Length && i < rows; i++)
        {
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, _lines[i], new CellStyle(attrs: StyleAttr.Dim | StyleAttr.Italic));
        }
    }

    public string RawText() => _text.ToString();

    private void EnsureWrapped(int width)
    {
        if (_width != width || _lines.Length == 0 && _text.Length > 0)
        {
            _width = width;
            var list = new List<string>(Math.Max(1, _lines.Length));
            TextWrap.WrapDocument(_text.ToString(), Math.Max(1, width), list);
            _lines = [.. list];
        }
    }
}

/// <summary>Finalized thinking block: renders committed reasoning text with
/// dim+italic styling, wrapped to the available width.</summary>
public sealed class ThinkingBlock : IChatBlock
{
    private readonly WrappedText _text;

    public ThinkingBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "thinking";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 48 + (_text.SourceLength * 2);

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(Math.Max(1, _text.GetLines(Math.Max(1, width)).Length));

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.Source, Math.Max(1, width));

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        var lines = _text.GetLines(Math.Max(1, ctx.Rect.Width));
        int rows = ctx.Rect.Height;
        for (int i = 0; i < lines.Length && i < rows; i++)
        {
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, lines.Span[i], new CellStyle(attrs: StyleAttr.Dim | StyleAttr.Italic));
        }
    }

    public string RawText() => _text.Source;
}
