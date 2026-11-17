using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Chat adapter over the domain-neutral <see cref="IVirtualListDelegate"/> seam.
///
/// <see cref="Count"/> is already public on the main partial and satisfies the
/// interface implicitly. This part supplies the other two halves — measurement
/// and rendering of one item — so a generic virtualized host can drive the
/// timeline without knowing what a chat block is. The timeline's own
/// <see cref="Paint"/> stays specialized (entrance motion, gate pulses,
/// separators and inline images); <see cref="Render"/> is the lean path a
/// neutral host uses and forwards those same flags from this instance.
/// No state is added, so the frame path remains allocation-free.
/// </summary>
public sealed partial class VirtualizedChatTimeline : IVirtualListDelegate
{
    /// <inheritdoc />
    public int EstimateHeight(int index, int width) => _cache.BlockAt(index).CheapEstimate(width);

    /// <inheritdoc />
    public VirtualItemMeasure Measure(int index, int width)
    {
        var m = _cache.BlockAt(index).Measure(width);
        return m.IsExact
            ? VirtualItemMeasure.Exact(m.MaxLines)
            : VirtualItemMeasure.Estimate(m.MinLines, m.MaxLines);
    }

    /// <inheritdoc />
    public void Render(int index, in VirtualItemPaintContext context)
    {
        var ctx = new BlockPaintContext(
            context.Buffer,
            context.Rect,
            context.Tick,
            context.SkipRows,
            ShowSeparators,
            InlineImages);
        _cache.BlockAt(index).Paint(ctx);
    }
}
