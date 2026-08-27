using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

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

    /// <summary>Byte budget for resident history; oldest blocks evict first.</summary>
    public long BudgetBytes { get; set; } = TimelineRing.DefaultBudgetBytes;

    /// <summary>
    /// Enables HDS v1 entrance motion for blocks appended while the feed is
    /// already visible: slide-up (<see cref="PanelFx.SlideMs" />) plus fade
    /// (<see cref="PanelFx.FadeMs" />). Blocks present at the first frame
    /// render settled, so initial screens stay pixel-stable. Off by default;
    /// hosts opt in via <see cref="EnableEntranceFx" />.
    /// </summary>
    public void EnableEntranceFx() => _entranceFx = true;

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
        _dirtyGeometry = true;

        if (BudgetBytes > 0)
        {
            long used = 0;
            for (int i = 0; i < _cache.Count; i++)
            {
                used += Math.Max(0, _cache.BlockAt(i).BudgetBytes);
            }

            while (_cache.Count > 1 && used > BudgetBytes)
            {
                var evicted = _cache.BlockAt(0);
                _ = _cache.EvictFirst();
                _ = _entranceStarts.Remove(evicted);
                used -= Math.Max(0, evicted.BudgetBytes);
                _dirtyGeometry = true;
            }
        }

        MarkLastDirty();
    }

    /// <summary>Swaps the live-stream placeholder for its committed form.</summary>
    public void ReplaceLast(IChatBlock block)
    {
        if (_cache.Count == 0)
        {
            Append(block);
            return;
        }

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

        SetScrollY(ScrollY + lines);
    }

    public void PageUp(int viewportHeight) => ScrollBy(-Math.Max(1, viewportHeight - 1));

    public void PageDown(int viewportHeight) => ScrollBy(Math.Max(1, viewportHeight - 1));

    public void ScrollToTop()
    {
        FollowTail = false;
        SetScrollY(0);
    }

    /// <summary>Snaps to the bottom and re-engages follow mode.</summary>
    public void ScrollToEnd(int viewportHeight)
    {
        FollowTail = true;
        SetScrollY(TotalHeightAfter(viewportHeight));
    }

    private long TotalHeightAfter(int viewportH) => Math.Max(0, TotalHeight - viewportH);

    private void SetScrollY(long y) => ScrollY = Math.Max(0, y);

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

        var (first, last) = VisibleRange(rect.Height);
        for (int i = first; i <= last; i++)
        {
            long blockTop = _cache.BlockTop(i);
            long relTop = blockTop - ScrollY;
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
