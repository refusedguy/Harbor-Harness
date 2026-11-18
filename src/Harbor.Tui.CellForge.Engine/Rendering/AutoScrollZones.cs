// Issue #2493: edge bands for drag auto-scroll (Textual get_auto_scroll_regions port).
// Pure geometry: no timer, no input event, no buffer.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// The two non-overlapping edge bands of a viewport that trigger auto-scroll
/// while a mouse selection is being dragged. Geometry only — the caller
/// decides how and when a shift is applied (issue #2493).
/// </summary>
public readonly struct AutoScrollZones
{
    /// <summary>
    /// Builds the top and bottom bands of <paramref name="viewport"/>. Each
    /// band is at most half the viewport height, so the two never overlap,
    /// even when <paramref name="lines"/> exceeds that half.
    /// </summary>
    public AutoScrollZones(Rect viewport, int lines)
    {
        Up = default;
        Down = default;

        if (viewport.Width <= 0 || viewport.Height <= 0 || lines <= 0)
        {
            return;
        }

        int band = Math.Min(lines, viewport.Height / 2);
        if (band <= 0)
        {
            return;
        }

        Up = new Rect(viewport.X, viewport.Y, viewport.Width, band);
        Down = new Rect(viewport.X, viewport.Bottom - band, viewport.Width, band);
    }

    /// <summary>Top band; a pointer here scrolls the viewport up.</summary>
    public Rect Up { get; }

    /// <summary>Bottom band; a pointer here scrolls the viewport down.</summary>
    public Rect Down { get; }

    /// <summary>True when no scrollable band could be formed.</summary>
    public bool IsEmpty => Up.Width <= 0 || Up.Height <= 0;

    /// <summary>True when the cell is inside the top band.</summary>
    public bool IsUp(int x, int y) => Up.Contains(x, y);

    /// <summary>True when the cell is inside the bottom band.</summary>
    public bool IsDown(int x, int y) => Down.Contains(x, y);

    /// <summary>
    /// Signed row shift for a pointer cell: -1 in the top band, +1 in the
    /// bottom band, and 0 outside both. One row per drag event; no timer.
    /// </summary>
    public int ShiftFor(int x, int y) => IsUp(x, y) ? -1 : IsDown(x, y) ? 1 : 0;
}
