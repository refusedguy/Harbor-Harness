namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Detachable scrollbar descriptor (#1206, epic #1195): any scrollable widget
/// yields a <c>(Total, Offset, Viewport)</c> triple and the shared
/// <see cref="Scrollbar"/> indicator renders on top of the same area
/// (ratatui <c>scrollbar.rs:145</c> + <c>email.rs:render_inbox</c> analog).
/// The indicator owns no scroll state — the widget keeps its own offset
/// (<see cref="ScrollableViewport"/>, <c>GenericNavState</c>, …) and only
/// lends the triple per frame. Allocation-free and AOT-safe.
/// </summary>
public readonly record struct ScrollInfo(long Total, long Offset, int Viewport)
{
    /// <summary>True when the content overflows the viewport (indicator warranted).</summary>
    public bool IsScrollable => ScrollableViewport.MaxOffsetFor(Total, Viewport) > 0;
}

/// <summary>
/// Convention surface for the detachable indicator: a scrollable widget
/// exposes its current <see cref="ScrollInfo"/>; hosts paint it via
/// <see cref="Scrollbar.TryPaint(ScreenBuffer, Harbor.Ui.Framework.Rendering.Rect, ScrollInfo)"/>.
/// </summary>
public interface IScrollInfoProvider
{
    /// <summary>Current scroll triple (total content, offset, viewport height).</summary>
    ScrollInfo GetScrollInfo();
}
