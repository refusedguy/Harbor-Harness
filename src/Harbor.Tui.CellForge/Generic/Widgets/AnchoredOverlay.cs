// Issue #2377: cell-anchored popup geometry and focus trap. Popover/Menu/
// DropdownButton from gpui-kit-components.md share ONE anchor contract: a rect
// glued to a terminal cell, flipped to the opposite side when it does not fit
// and clamped to the viewport. The host paints whatever content it puts in the
// bounds; this type owns no menu items, no store and no plugin axis.
//
// It is NOT the command palette primitive (#2321): no fuzzy search, no alias
// table and no input history live here. A palette is a modal query over a
// command catalog; an anchored overlay is a rectangle next to a cell.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Side of the anchor cell the popup prefers to occupy.</summary>
public enum AnchorSide : byte
{
    Bottom,
    Top,
    Right,
    Left,
}

/// <summary>A terminal cell in screen coordinates (column, row).</summary>
/// <param name="Column">Zero-based column.</param>
/// <param name="Row">Zero-based row.</param>
public readonly record struct AnchorCell(int Column, int Row);

/// <summary>Desired popup size in cells, before viewport clamping.</summary>
/// <param name="Width">Preferred width.</param>
/// <param name="Height">Preferred height.</param>
public readonly record struct OverlaySize(int Width, int Height);

/// <summary>
/// State of one anchored popup: where it sits, which side won, whether it is
/// open and which of its focus slots is active. Tab and Shift+Tab cycle the
/// slots, Escape closes it; every other key is left to the host. The host
/// keeps the popup modal in its own overlay stack, so keys never reach the
/// screen behind it while <see cref="IsOpen"/> is true.
/// </summary>
public sealed class AnchoredOverlay
{
    private int _focusIndex;

    /// <summary>Creates a popup at <paramref name="anchor"/> inside <paramref name="viewport"/>.</summary>
    public AnchoredOverlay(
        AnchorCell anchor,
        OverlaySize content,
        Rect viewport,
        AnchorSide preferred = AnchorSide.Bottom,
        int gap = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        Place(anchor, content, viewport, preferred, gap);
    }

    /// <summary>Cell the popup is glued to.</summary>
    public AnchorCell Anchor { get; private set; }

    /// <summary>Size the popup would like to have.</summary>
    public OverlaySize Content { get; private set; }

    /// <summary>Screen the popup must stay inside.</summary>
    public Rect Viewport { get; private set; }

    /// <summary>Side the host asked for.</summary>
    public AnchorSide Preferred { get; private set; }

    /// <summary>Empty cells between the anchor cell and the popup.</summary>
    public int Gap { get; private set; }

    /// <summary>Resolved screen rect, clamped to the viewport.</summary>
    public Rect Bounds { get; private set; }

    /// <summary>Side actually used after flipping.</summary>
    public AnchorSide Side { get; private set; }

    /// <summary>True when the anchor did not have room on the preferred side.</summary>
    public bool Flipped => Side != Preferred;

    /// <summary>Whether the popup is showing.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Number of focusable slots inside the popup.</summary>
    public int FocusCount { get; private set; }

    /// <summary>Active slot, or -1 while there are no slots.</summary>
    public int FocusIndex => _focusIndex;

    /// <summary>Repositions the popup and recomputes <see cref="Bounds"/> and <see cref="Side"/>.</summary>
    public void Place(
        AnchorCell anchor,
        OverlaySize content,
        Rect viewport,
        AnchorSide? preferred = null,
        int? gap = null)
    {
        if (gap.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(gap.Value);
        }

        Anchor = anchor;
        Content = content;
        Viewport = viewport;
        Preferred = preferred ?? Preferred;
        Gap = gap ?? Gap;
        Bounds = Resolve(anchor, content, viewport, Preferred, Gap, out var side);
        Side = side;
    }

    /// <summary>Opens the popup; focus starts at its first slot.</summary>
    public void Open()
    {
        if (FocusCount > 0 && _focusIndex < 0)
        {
            _focusIndex = 0;
        }

        IsOpen = true;
    }

    /// <summary>Closes the popup.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Declares how many slots the focus trap contains.</summary>
    public void SetFocusCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        FocusCount = count;
        _focusIndex = count == 0 ? -1 : (_focusIndex < 0 ? 0 : Math.Min(_focusIndex, count - 1));
    }

    /// <summary>Moves the active slot with wraparound. No-op without slots.</summary>
    public void MoveFocus(int delta)
    {
        if (FocusCount == 0)
        {
            _focusIndex = -1;
            return;
        }

        _focusIndex = ((_focusIndex + delta) % FocusCount + FocusCount) % FocusCount;
    }

    /// <summary>Points the trap at a slot chosen by the host (e.g. a menu row).</summary>
    public void FocusSlot(int index)
    {
        if (FocusCount > 0)
        {
            _focusIndex = ((index % FocusCount) + FocusCount) % FocusCount;
        }
    }

    /// <summary>True when the popup is open and <paramref name="col"/>/<paramref name="row"/> land in it.</summary>
    public bool Contains(int col, int row) => IsOpen && Bounds.Contains(col, row);

    /// <summary>
    /// Routes a focus key: Tab forward, Shift+Tab backward, Escape close.
    /// Press and repeat only. Anything else is left to the host. Returns true
    /// when consumed.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
        {
            return false;
        }

        if (key.Key == KeyCode.Escape && key.Modifiers == KeyModifiers.None)
        {
            Close();
            return true;
        }

        if (key.Key != KeyCode.Tab)
        {
            return false;
        }

        if (key.Modifiers == KeyModifiers.None)
        {
            MoveFocus(1);
            return FocusCount > 0;
        }

        if (key.Modifiers == KeyModifiers.Shift)
        {
            MoveFocus(-1);
            return FocusCount > 0;
        }

        return false;
    }

    /// <summary>
    /// Resolves the popup rect. The preferred side wins when the content fits;
    /// otherwise the opposite side on that axis is tried, then the side with
    /// more room. Widths/heights are clamped to the viewport and the anchor
    /// cell is never covered.
    /// </summary>
    public static Rect Resolve(
        AnchorCell anchor,
        OverlaySize content,
        Rect viewport,
        AnchorSide preferred,
        int gap,
        out AnchorSide actual)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        int width = Math.Clamp(content.Width, 0, Math.Max(0, viewport.Width));
        int height = Math.Clamp(content.Height, 0, Math.Max(0, viewport.Height));

        int below = viewport.Bottom - (anchor.Row + 1 + gap);
        int above = (anchor.Row - gap) - viewport.Y;
        int right = viewport.Right - (anchor.Column + 1 + gap);
        int left = (anchor.Column - gap) - viewport.X;

        switch (preferred)
        {
            case AnchorSide.Top:
                actual = Prefers(height <= above, height <= below, above, below, AnchorSide.Top, AnchorSide.Bottom);
                break;
            case AnchorSide.Right:
                actual = Prefers(width <= right, width <= left, right, left, AnchorSide.Right, AnchorSide.Left);
                break;
            case AnchorSide.Left:
                actual = Prefers(width <= left, width <= right, left, right, AnchorSide.Left, AnchorSide.Right);
                break;
            default:
                actual = Prefers(height <= below, height <= above, below, above, AnchorSide.Bottom, AnchorSide.Top);
                break;
        }

        int w = width;
        int h = height;
        int x;
        int y;
        switch (actual)
        {
            case AnchorSide.Top:
                h = Math.Min(h, Math.Max(0, above));
                x = anchor.Column;
                y = anchor.Row - gap - h;
                break;
            case AnchorSide.Right:
                w = Math.Min(w, Math.Max(0, right));
                x = anchor.Column + 1 + gap;
                y = anchor.Row;
                break;
            case AnchorSide.Left:
                w = Math.Min(w, Math.Max(0, left));
                x = anchor.Column - gap - w;
                y = anchor.Row;
                break;
            default:
                h = Math.Min(h, Math.Max(0, below));
                x = anchor.Column;
                y = anchor.Row + 1 + gap;
                break;
        }

        if (w <= 0 || h <= 0)
        {
            return default;
        }

        x = Math.Clamp(x, viewport.X, viewport.Right - w);
        y = Math.Clamp(y, viewport.Y, viewport.Bottom - h);
        return new Rect(x, y, w, h);
    }

    private static AnchorSide Prefers(
        bool preferredFits,
        bool oppositeFits,
        int preferredRoom,
        int oppositeRoom,
        AnchorSide preferred,
        AnchorSide opposite)
    {
        if (preferredFits)
        {
            return preferred;
        }

        if (oppositeFits)
        {
            return opposite;
        }

        return oppositeRoom > preferredRoom ? opposite : preferred;
    }
}
