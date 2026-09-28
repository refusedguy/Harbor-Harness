namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Reusable scrollable-viewport core ([PRIM3a], part of #288): a clamped
/// content offset plus the clipped visible slice. Generalized out of
/// <c>TimelineLayoutCache.MaxScrollFor/ClampScrollY</c> and the scroll math
/// in <see cref="VirtualizedChatTimeline"/> so any tall list (timeline,
/// command palette, …) shares one bounds formula instead of re-deriving
/// <c>max(0, total - viewportH)</c> per widget.
///
/// Content-agnostic: rows are uniform address space here. Variable-height
/// layout (estimates, measure/settle, anchors) stays in
/// <see cref="TimelineLayoutCache"/>. Allocation-free and AOT-safe.
/// </summary>
public sealed class ScrollableViewport
{
    private long _totalContent;
    private int _viewportH;
    private long _offset;

    /// <summary>Total content rows (uniform row space).</summary>
    public long TotalContent => _totalContent;

    /// <summary>Visible height in rows.</summary>
    public int ViewportH => _viewportH;

    /// <summary>Top row of the viewport in content space — always clamped.</summary>
    public long Offset => _offset;

    /// <summary>Largest legal offset: <c>max(0, TotalContent - ViewportH)</c>.</summary>
    public long MaxOffset => MaxOffsetFor(_totalContent, _viewportH);

    /// <summary>True when pinned to the first row.</summary>
    public bool AtTop => _offset <= 0;

    /// <summary>True when pinned to the last visible window.</summary>
    public bool AtBottom => _offset >= MaxOffset;

    /// <summary>
    /// Largest legal offset for the given content total and viewport height.
    /// Pure and allocation-free; negative inputs are treated as zero.
    /// </summary>
    public static long MaxOffsetFor(long totalContent, int viewportH) =>
        Math.Max(0, totalContent - Math.Max(0, viewportH));

    /// <summary>
    /// Clamps an offset to <c>[0 .. MaxOffsetFor(totalContent, viewportH)]</c>.
    /// Pure and allocation-free.
    /// </summary>
    public static long ClampOffsetFor(long offset, long totalContent, int viewportH) =>
        Math.Clamp(offset, 0, MaxOffsetFor(totalContent, viewportH));

    /// <summary>Reports a new content total; the offset is re-clamped.</summary>
    public void SetTotal(long totalContent)
    {
        _totalContent = Math.Max(0, totalContent);
        _offset = Math.Clamp(_offset, 0, MaxOffset);
    }

    /// <summary>Reports a new viewport height (negative treated as zero); the offset is re-clamped.</summary>
    public void Resize(int viewportH)
    {
        _viewportH = Math.Max(0, viewportH);
        _offset = Math.Clamp(_offset, 0, MaxOffset);
    }

    /// <summary>Sets both geometry at once; the offset is re-clamped. Returns the clamped offset.</summary>
    public long Configure(long totalContent, int viewportH)
    {
        _totalContent = Math.Max(0, totalContent);
        _viewportH = Math.Max(0, viewportH);
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        return _offset;
    }

    /// <summary>Jumps to an absolute offset (clamped). Returns the clamped offset.</summary>
    public long SetOffset(long offset)
    {
        _offset = Math.Clamp(offset, 0, MaxOffset);
        return _offset;
    }

    /// <summary>Moves by a signed delta (clamped). Returns the clamped offset.</summary>
    public long ScrollBy(long delta) => SetOffset(_offset + delta);

    /// <summary>Pins to the first row.</summary>
    public void ScrollToTop() => _offset = 0;

    /// <summary>Pins to the last visible window.</summary>
    public void ScrollToEnd() => _offset = MaxOffset;

    /// <summary>
    /// Clipped visible slice in content space: <c>[Offset .. min(Total, Offset + ViewportH))</c>.
    /// Empty when the viewport or the content is empty (<c>First == LastExclusive</c>).
    /// </summary>
    public (long First, long LastExclusive) VisibleSlice()
    {
        long first = _offset;
        long last = Math.Min(_totalContent, _offset + (long)_viewportH);
        return last < first ? (first, first) : (first, last);
    }

    /// <summary>Number of content rows in <see cref="VisibleSlice"/>.</summary>
    public long VisibleCount
    {
        get
        {
            var (first, last) = VisibleSlice();
            return last - first;
        }
    }
}
