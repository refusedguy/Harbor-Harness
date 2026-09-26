using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Console-size viewport adapter over the projected screen model (the widgets
/// §3.3 companion to <see cref="Widgets.VirtualizedChatTimeline"/>): tracks how
/// many transcript rows exist (<see cref="TotalLines"/>), how many fit on screen
/// (<see cref="ViewportLines"/>) and how far the view is lifted off the live tail
/// (<see cref="ScrollOffset"/>, 0 = pinned to newest, grows toward the top).
/// The scroll pin/unpin contract mirrors
/// <see cref="Widgets.VirtualizedChatTimeline.ScrollBy"/> 1:1 — scrolling up
/// unpins the tail, scrolling down never re-pins, only <see cref="ScrollToEnd"/>
/// re-engages follow mode. BCL-only, AOT-clean, allocation-free on
/// <see cref="Apply"/>.
/// </summary>
public sealed class CellForgeViewport : IUiViewport
{
    /// <summary>Fallback width when the console reports a non-positive value.</summary>
    public const int DefaultWidth = 80;

    /// <summary>Fallback height when the console reports a non-positive value.</summary>
    public const int DefaultHeight = 24;

    /// <summary>Console width in cells.</summary>
    public int Width { get; private set; }

    /// <summary>Console height in cells.</summary>
    public int Height { get; private set; }

    /// <summary>Number of history rows visible at once (reported by the renderer).</summary>
    public int ViewportLines { get; private set; }

    /// <summary>Rows lifted off the live tail (0 = pinned to newest).</summary>
    public long ScrollOffset { get; private set; }

    /// <summary>Total wrapped history rows (reported by <see cref="Apply"/>).</summary>
    public long TotalLines { get; private set; }

    /// <summary>True while stuck to the live tail (default).</summary>
    public bool FollowTail { get; private set; } = true;

    /// <summary>
    /// Create a viewport for the given console size. Non-positive dimensions
    /// fall back to <see cref="DefaultWidth"/> × <see cref="DefaultHeight"/>.
    /// </summary>
    public CellForgeViewport(int width, int height, long scrollOffset = 0)
    {
        Width = width <= 0 ? DefaultWidth : width;
        Height = height <= 0 ? DefaultHeight : height;
        ViewportLines = Height;
        ScrollOffset = Math.Clamp(scrollOffset, 0, MaxScrollOffset);
    }

    /// <summary>
    /// Read the live console size (same per-property try/catch as
    /// <see cref="CellForgeRenderContext"/>); non-positive values fall back to
    /// <see cref="DefaultWidth"/> × <see cref="DefaultHeight"/> via the ctor.
    /// </summary>
    public static CellForgeViewport FromConsole()
    {
        var (width, height) = ReadConsoleSize();
        return new CellForgeViewport(width, height);
    }

    /// <summary>Re-read the live console size; keeps the scroll position clamped.</summary>
    public void RefreshFromConsole()
    {
        var (width, height) = ReadConsoleSize();
        Width = width <= 0 ? DefaultWidth : width;
        Height = height <= 0 ? DefaultHeight : height;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, MaxScrollOffset);
    }

    /// <summary>Apply a new console size (non-positive values fall back to defaults).</summary>
    public void Resize(int width, int height)
    {
        Width = width <= 0 ? DefaultWidth : width;
        Height = height <= 0 ? DefaultHeight : height;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, MaxScrollOffset);
    }

    /// <summary>Report how many history rows fit on screen; clamps the offset.</summary>
    public void SetViewportLines(int lines)
    {
        ViewportLines = Math.Max(0, lines);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, MaxScrollOffset);
    }

    /// <summary>
    /// Fold the projected screen into the viewport. Zero-alloc: only
    /// <c>RenderedLines.Count</c> is read, nothing is enumerated or copied.
    /// A pinned viewport snaps to the live tail; an unpinned one keeps its
    /// offset, clamped to the new range.
    /// </summary>
    public void Apply(UiScreenModel screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        TotalLines = screen.Transcript.RenderedLines.Count;
        ScrollOffset = FollowTail ? 0 : Math.Clamp(ScrollOffset, 0, MaxScrollOffset);
    }

    /// <summary>
    /// Move the view; 1:1 with <see cref="Widgets.VirtualizedChatTimeline.ScrollBy"/>:
    /// negative (up) unpins the tail, positive (down) never re-pins — only
    /// <see cref="ScrollToEnd"/> does. The offset stays within
    /// <c>[0, <see cref="MaxScrollOffset"/>]</c>.
    /// </summary>
    public void ScrollBy(int lines)
    {
        if (lines < 0)
        {
            FollowTail = false;
        }

        ScrollOffset = Math.Clamp(ScrollOffset - lines, 0, MaxScrollOffset);
    }

    /// <summary>Jump to the oldest row and unpin the tail.</summary>
    public void ScrollToTop()
    {
        FollowTail = false;
        ScrollOffset = MaxScrollOffset;
    }

    /// <summary>Snap to the live tail and re-engage follow mode.</summary>
    public void ScrollToEnd()
    {
        FollowTail = true;
        ScrollOffset = 0;
    }

    /// <summary>Largest valid <see cref="ScrollOffset"/> (0 when everything fits).</summary>
    public long MaxScrollOffset => Math.Max(0, TotalLines - ViewportLines);

    /// <summary>Index of the first visible row in virtual (top-down) space.</summary>
    public long FirstVisibleRow => Math.Max(0, TotalLines - ViewportLines - ScrollOffset);

    /// <summary>How many rows are actually visible (fewer than requested when short).</summary>
    public int VisibleRowCount => (int)Math.Max(0, Math.Min((long)ViewportLines, TotalLines));

    /// <summary>
    /// How far the history is scrolled, as a percentage (0 = bottom/live,
    /// 100 = top). Same formula as <c>UiState.ScrollPercent</c>.
    /// </summary>
    public int ScrollPercent
    {
        get
        {
            long max = MaxScrollOffset;
            if (max == 0)
            {
                return 0;
            }

            return (int)Math.Round(100.0 * ScrollOffset / max);
        }
    }

    private static (int Width, int Height) ReadConsoleSize()
    {
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch
        {
            width = 0;
        }

        int height;
        try
        {
            height = Console.WindowHeight;
        }
        catch
        {
            height = 0;
        }

        return (width, height);
    }
}
