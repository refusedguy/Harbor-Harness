// PanelSnapRule.cs — K17 (issue #2683): resizable dock panels with snap.
//
// The drawer can be dragged to any width between a snap-open floor and a
// maximum. Dragging past the drag floor and releasing snaps the panel CLOSED;
// releasing anywhere inside the usable band keeps it open at that width, and
// reopening restores the default. This file owns that decision as pure data so
// it can be asserted without standing up an Avalonia window. The view only
// forwards pointer deltas and applies the result.

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>The width band a resizable dock panel is allowed to occupy.</summary>
/// <param name="DefaultWidth">Width restored when the panel is reopened.</param>
/// <param name="MinWidth">Snap-open floor: a release just below it grows to here.</param>
/// <param name="MaxWidth">Ceiling a drag cannot exceed.</param>
/// <param name="SnapClosedWidth">
///     Drag floor. Releasing at or below it snaps the panel closed instead of
///     leaving a sliver of a panel on screen.
/// </param>
public readonly record struct PanelResizeConstraints(
    double DefaultWidth,
    double MinWidth,
    double MaxWidth,
    double SnapClosedWidth);

/// <summary>The width to apply and whether the panel stays open after a drag.</summary>
public readonly record struct PanelSnapResult(double Width, bool IsOpen);

/// <summary>
///     Decides how a dragged dock panel settles. No Avalonia types, no dispatch:
///     the same rule drives the drawer in the desktop shell and is fully
///     unit-testable here.
/// </summary>
public static class PanelSnapRule
{
    /// <summary>The shipped right-drawer band, in device-independent pixels.</summary>
    public static PanelResizeConstraints DrawerConstraints { get; } = new(
        DefaultWidth: 360,
        MinWidth: 220,
        MaxWidth: 720,
        SnapClosedWidth: 140);

    /// <summary>
    ///     Clamp the width used WHILE dragging. The drag floor is deliberately
    ///     lower than <see cref="PanelResizeConstraints.MinWidth" /> so the user
    ///     can reach the snap-closed band before releasing; the final snap is
    ///     decided by <see cref="Resolve" />.
    /// </summary>
    public static double ClampDragWidth(double proposed, PanelResizeConstraints c)
    {
        if (double.IsNaN(proposed) || double.IsInfinity(proposed)) return c.DefaultWidth;
        return Math.Clamp(proposed, c.SnapClosedWidth, c.MaxWidth);
    }

    /// <summary>
    ///     Final decision on pointer release. A release in the snap-closed band
    ///     closes the panel and restores its default width, so reopening always
    ///     starts from a usable size. A release just under the minimum grows to
    ///     the minimum; anything within the band is kept; anything past the
    ///     maximum is clamped back to it.
    /// </summary>
    public static PanelSnapResult Resolve(double proposed, PanelResizeConstraints c)
    {
        if (double.IsNaN(proposed) || double.IsInfinity(proposed))
            return new PanelSnapResult(c.DefaultWidth, false);

        if (proposed <= c.SnapClosedWidth)
            return new PanelSnapResult(c.DefaultWidth, false);

        if (proposed < c.MinWidth)
            return new PanelSnapResult(c.MinWidth, true);

        if (proposed > c.MaxWidth)
            return new PanelSnapResult(c.MaxWidth, true);

        return new PanelSnapResult(proposed, true);
    }
}
