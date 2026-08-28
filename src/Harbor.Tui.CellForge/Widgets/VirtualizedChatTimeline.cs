using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// The virtualized chat feed (widgets §3.3): renders only the blocks that
/// intersect the viewport (lazygit viewport-only), follows the tail while the
/// user is pinned to it and unpins on any upward scroll. Storage, heights and
/// virtual geometry live in <see cref="TimelineLayoutCache"/>.
/// </summary>
public sealed class VirtualizedChatTimeline
{
    private readonly TimelineLayoutCache _cache = new();
    private readonly Dictionary<IChatBlock, long> _entranceStarts = new();
    private int _lastWidth = -1;
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
                _cache.MarkHeightsDirty(i);
                return;
            }
        }
    }

    /// <summary>Streaming tail grew — last block's cached height is stale.</summary>
    public void MarkLastDirty() => _cache.MarkHeightsDirty(Math.Max(0, _cache.Count - 1));

    public void ScrollUp(int lines) => ScrollBy(-lines);

    public void ScrollDown(int lines) => ScrollBy(lines);

    public void ScrollBy(int lines)
    {
        if (lines < 0)
        {
            FollowTail = false;
        }

        long fromVisual = _scrollAnimating ? (long)Math.Round(_visualScrollY) : ScrollY;
        SetScrollY(ScrollY + lines);
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

    private long TotalHeightAfter(int viewportH) => Math.Max(0, TotalHeight - viewportH);

    private void SetScrollY(long y)
    {
        ScrollY = Math.Max(0, y);
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

    /// <summary>Runs layout for this frame; resolves follow-tail and anchors.</summary>
    public LayoutOutcome PrepareFrame(int width, int viewportH)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportH);

        if (_lastWidth != width && _cache.Count > 0)
        {
            _cache.PinAnchor(ScrollY);
        }

        if (FollowTail)
        {
            ScrollY = TotalHeightAfter(viewportH);
        }

        var outcome = _cache.PrepareLayout(width, viewportH, ScrollY);
        _lastWidth = width;
        _dirtyGeometry = false;

        if (outcome == LayoutOutcome.FullRebuild && _cache.Count > 0)
        {
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
            bool animating = _entranceStarts.TryGetValue(block, out long startTick);
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

            if (animating && alpha < 1.0)
            {
                PanelFx.BlendRegion(buffer, new Rect(rect.X, paintY, rect.Width, Math.Max(1, paintH)), alpha);
            }
        }
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
