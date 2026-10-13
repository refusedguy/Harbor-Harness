namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// External navigation state for generic list-like widgets (epic #1195, #1198).
/// Ratatui analog: <c>ListState</c>/<c>TableState</c> (<c>state.rs</c>) rendered via
/// <c>StatefulWidget::render(area, buf, state)</c> — the widget is immutable, the
/// state object survives frames and owns selection + scroll offset.
/// Ours before: <see cref="GenericSelectionList"/> (own <c>_selected</c>+<c>_offset</c>),
/// <c>DialogSelectList</c> (own <c>ListPromptState</c>+<c>ScrollableViewport</c>),
/// <c>CommandPaletteView</c> (own <c>_selected</c>+<c>_viewport</c>) — each its own
/// selection+scroll велосипед. This type is the unified nav half; content stays
/// with the widget, position lives here.
/// </summary>
public sealed class GenericNavState
{
    private int _selected;
    private int _offset;
    private int _count;
    private int _viewportH = 1;

    /// <summary>Selected index (clamped; -1 when empty).</summary>
    public int Selected => _count == 0 ? -1 : Math.Clamp(_selected, 0, _count - 1);

    /// <summary>First visible row (clamped to the current content/viewport).</summary>
    public int Offset => Math.Clamp(_offset, 0, MaxOffset);

    /// <summary>Number of navigable rows.</summary>
    public int Count => _count;

    /// <summary>Visible rows the last <see cref="Sync"/> saw. At least 1.</summary>
    public int ViewportH => _viewportH;

    /// <summary>True when the selection sits on the last row.</summary>
    public bool AtBottom => Selected >= _count - 1;

    /// <summary>True when the selection sits on the first row (or the list is empty).</summary>
    public bool AtTop => Selected <= 0;

    private int MaxOffset => Math.Max(0, _count - Math.Max(1, _viewportH));

    /// <summary>
    /// Declares the content length (and optionally the viewport height).
    /// Resets selection to the first row and scroll to the top — the same
    /// reset <c>SetItems</c> paths already performed inline.
    /// </summary>
    public void SetContent(int count, int? viewportH = null)
    {
        _count = Math.Max(0, count);
        if (viewportH.HasValue)
        {
            _viewportH = Math.Max(1, viewportH.Value);
        }

        _selected = 0;
        _offset = 0;
    }

    /// <summary>Records the painted viewport height and re-clamps the offset.</summary>
    public void Sync(int viewportH)
    {
        _viewportH = Math.Max(1, viewportH);
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        EnsureVisible();
    }

    /// <summary>Selects an index (clamped). No-op when empty.</summary>
    public void MoveTo(int index)
    {
        if (_count == 0)
        {
            return;
        }

        _selected = Math.Clamp(index, 0, _count - 1);
        EnsureVisible();
    }

    /// <summary>Moves the selection by a signed delta (clamped, no wrap).</summary>
    public void MoveBy(int delta)
    {
        if (_count == 0 || delta == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _count - 1);
        EnsureVisible();
    }

    /// <summary>Selects the next row (clamped at the end).</summary>
    public void SelectNext() => MoveBy(1);

    /// <summary>Selects the previous row (clamped at the start).</summary>
    public void SelectPrevious() => MoveBy(-1);

    /// <summary>Selects the first row. No-op when empty.</summary>
    public void SelectFirst() => MoveTo(0);

    /// <summary>Selects the last row. No-op when empty.</summary>
    public void SelectLast() => MoveTo(_count - 1);

    /// <summary>Scrolls the viewport by a signed delta (clamped; selection untouched).</summary>
    public void ScrollBy(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        _offset = Math.Clamp(_offset + delta, 0, MaxOffset);
    }

    /// <summary>Scrolls to the top (selection untouched).</summary>
    public void ScrollToTop() => _offset = 0;

    /// <summary>Scrolls to the bottom (selection untouched).</summary>
    public void ScrollToEnd() => _offset = MaxOffset;

    /// <summary>First visible row and one-past-last visible row for the current viewport.</summary>
    public (int First, int LastExclusive) VisibleSlice() =>
        (_offset, Math.Min(_count, _offset + Math.Max(1, _viewportH)));

    /// <summary>Scrolls just enough to bring the selection into view.</summary>
    public void EnsureVisible()
    {
        if (_count == 0)
        {
            return;
        }

        int target = Math.Clamp(_selected, 0, _count - 1);
        int rows = Math.Max(1, _viewportH);
        if (target < _offset)
        {
            _offset = target;
        }
        else if (target >= _offset + rows)
        {
            _offset = target - rows + 1;
        }
    }
}

/// <summary>
/// Ratatui <c>StatefulWidget</c> analog (<c>stateful_widget.rs:124</c>, #1198):
/// an immutable widget painted with an external, frame-surviving
/// <typeparamref name="TState"/>. The widget owns content and geometry;
/// selection + scroll live in <paramref name="state"/>.
/// </summary>
/// <typeparam name="TState">External navigation state (e.g. <see cref="GenericNavState"/>).</typeparam>
public interface IStatefulGenericWidget<TState>
{
    /// <summary>Paints within <paramref name="rect"/> using <paramref name="state"/> for position.</summary>
    void Paint(ScreenBuffer buffer, Rect rect, TState state);
}
