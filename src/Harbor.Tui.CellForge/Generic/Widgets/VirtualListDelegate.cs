using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Height of one virtual item at a given width, in terminal rows. Exact when
/// the item settled its final wrapped height, an estimate otherwise. This is
/// the domain-neutral counterpart of the chat-only <c>BlockMeasure</c>: the
/// virtual list stores <see cref="BestGuess"/> until an item is visible, then
/// replaces it with the exact height.
/// </summary>
public readonly record struct VirtualItemMeasure(int MinHeight, int MaxHeight, bool IsExact)
{
    /// <summary>Authoritative height.</summary>
    public static VirtualItemMeasure Exact(int height) => new(height, height, true);

    /// <summary>Off-screen guess; the list keeps this until it can measure.</summary>
    public static VirtualItemMeasure Estimate(int min, int max) => new(min, max, false);

    /// <summary>Single-row floor; exact measures pass through.</summary>
    public int BestGuess => Math.Max(1, IsExact ? MinHeight : (MinHeight + MaxHeight) / 2);
}

/// <summary>
/// Paint input for one visible virtual item. Deliberately carries only the
/// cell-grid primitives: no chat, file or table vocabulary crosses the seam.
/// A domain adapter enriches this with its own flags when it paints.
/// </summary>
public readonly struct VirtualItemPaintContext
{
    public VirtualItemPaintContext(ScreenBuffer buffer, Rect rect, long tick, int skipRows = 0)
    {
        Buffer = buffer;
        Rect = rect;
        Tick = tick;
        SkipRows = Math.Max(0, skipRows);
    }

    /// <summary>Back buffer to paint into.</summary>
    public ScreenBuffer Buffer { get; }

    /// <summary>Clip rect for the item; X/Width are the column span.</summary>
    public Rect Rect { get; }

    /// <summary>Monotonic frame tick for animated items.</summary>
    public long Tick { get; }

    /// <summary>Rows clipped off the top when the viewport starts mid-item.</summary>
    public int SkipRows { get; }
}

/// <summary>
/// Domain-neutral data source for a virtualized list or table (gpui-kit
/// <c>ListDelegate</c>: <c>items_count</c> / <c>render_item</c>). The widget owns
/// geometry and scrolling; the delegate owns exactly three things: how many
/// items there are, how to measure one, and how to render one. Keeping those
/// three behind this interface lets the same virtualization serve a file list,
/// a log view, a diagnostics list or a table without a domain type in
/// <c>UiState</c> and without teaching the layout engine about any of them.
///
/// All members are index-based and synchronous: the delegate is a view over
/// already-projected data, so no async or allocation is introduced on the
/// frame path. Implementations must be stable within a frame — <see cref="Count"/>
/// and the height of an index may only change between frames, never mid-paint.
/// </summary>
public interface IVirtualListDelegate
{
    /// <summary>Number of items in the source. Stable within a frame.</summary>
    int Count { get; }

    /// <summary>
    /// Cheap off-screen height guess. Must not render, wrap or allocate; only
    /// <see cref="Measure"/> produces an authoritative height.
    /// </summary>
    int EstimateHeight(int index, int width);

    /// <summary>Authoritative height for an item at <paramref name="width"/>.</summary>
    VirtualItemMeasure Measure(int index, int width);

    /// <summary>Renders one visible item into the clipped context.</summary>
    void Render(int index, in VirtualItemPaintContext context);
}
