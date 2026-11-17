// #2360 (epic #1195): measured leaf node — the engine-owned port of GPUI's
// request_measured_layout (crates/gpui/src/taffy.rs:85, commit
// 698368515d17a602dd51ea61297e5412ce06f52f). A leaf carries a pure
// AvailableSpace -> Size closure; text blocks and tables report their own
// desired size instead of duplicating the wrapping/column math in the parent.
// Same leaf rule as every file in this directory: BCL-only, AOT-compatible,
// zero Harbor references, render-thread owned.
//
// BOUNDARY WITH #1221 (SizeConstraint dict + Fill weights + Auto intrinsic):
// there is exactly ONE owner of intrinsic size in the solver — FlexTrack.Auto
// and its two-pass `intrinsic` channel in FlexLayout. This leaf only supplies
// numbers: ToTrack() maps min-content to the Auto floor and max-content to the
// Auto cap. No second solver, no second constraint vocabulary. Fill weights,
// percent/ratio tracks and justification stay entirely in FlexLayout.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Childless node whose desired size is computed by a caller-supplied pure
/// closure over <see cref="AvailableSpace"/>. The closure is invoked with a
/// definite box during <see cref="Measure"/> and with the two semantic
/// requests on demand (<see cref="MinContent"/>, <see cref="MaxContent"/>).
/// Paint is a no-op unless a paint callback is supplied. Constructed once
/// (setup path); the steady-state Measure/Arrange/Paint frame allocates
/// nothing.
/// </summary>
public sealed class MeasuredLeaf : LeafNode
{
    private readonly Func<AvailableSpace, Size> _measure;
    private readonly Action<ScreenBuffer, Rect>? _paint;

    /// <summary>
    /// Creates a measured leaf from a pure measure closure.
    /// </summary>
    /// <param name="measure">Maps the offered space to the content's size.
    /// For a definite box it MUST clamp to the offered extents; for semantic
    /// requests it MUST branch on <see cref="AvailableSpace.Kind"/> and ignore
    /// the zero Width/Height. Pure: no buffer writes, no state mutation.</param>
    /// <param name="paint">Optional painter, confined to the leaf's arranged
    /// rect ∩ clip. Null makes <see cref="Paint"/> a no-op.</param>
    /// <exception cref="ArgumentNullException"><paramref name="measure"/> is null.</exception>
    public MeasuredLeaf(Func<AvailableSpace, Size> measure, Action<ScreenBuffer, Rect>? paint = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        _measure = measure;
        _paint = paint;
    }

    /// <summary>Smallest non-overflowing size (longest word / widest column).</summary>
    public Size MinContent => _measure(AvailableSpace.MinContent);

    /// <summary>Size with no wrapping (full line / sum of full columns).</summary>
    public Size MaxContent => _measure(AvailableSpace.MaxContent);

    /// <summary>Desired size inside a definite box, clamped by the solver's bounds.</summary>
    public override Size Measure(Constraints constraints) =>
        _measure(AvailableSpace.Definite(constraints.Width, constraints.Height));

    /// <summary>
    /// Seats this leaf in the one flex solver (#1221): the main-axis
    /// min-content becomes <see cref="FlexTrack.Auto"/>'s floor and max-content
    /// its cap. The solver still owns the clamp, the Fill split and the
    /// justification; this method only supplies the two intrinsic numbers.
    /// Normalized so floor &lt;= cap even for a degenerate closure.
    /// </summary>
    public FlexTrack ToTrack(SplitDir dir = SplitDir.Horizontal)
    {
        Size min = MinContent;
        Size max = MaxContent;
        int minMain = Math.Max(0, dir == SplitDir.Horizontal ? min.Width : min.Height);
        int maxMain = Math.Max(0, dir == SplitDir.Horizontal ? max.Width : max.Height);
        return FlexTrack.Auto(Math.Min(minMain, maxMain), Math.Max(minMain, maxMain));
    }

    /// <inheritdoc />
    public override void Paint(ScreenBuffer buffer, Rect clip) =>
        _paint?.Invoke(buffer, PaintArea(clip));
}
