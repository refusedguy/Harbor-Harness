// #1209 (epic #1195): cell Measure/Render contract — Spectre
// Rendering/{Measurement} + Widgets/Padder + IExpandable/IPaddable ported as
// segment-logic-to-cells. Source mapping:
//   Measurement  -> Spectre Rendering/Measurement.cs (min/max width in cells
//                   at a given available width; produced by the widget).
//   Padding      -> Spectre IPaddable insets + Widgets/Padder.cs Deflate math
//                   (content rect inside chrome; pure rect math, no wrapping
//                   renderable — there is nothing to wrap on the cell grid).
//   Expand flag  -> Spectre IExpandable ("take the remaining space"), seated
//                   onto the existing solver via WidgetMeasure.ToTrack
//                   (expand = FlexTrack.Min floor, fixed = FlexTrack.Fixed fit).
// Composition stays pure model->cells functions: no buffer writes, no cursor
// escapes (FRONT==BACK invariant — DiffEngine owns every escape), no state.
// Same leaf rule as every file in this directory: BCL-only, AOT-compatible,
// zero Harbor references, render-thread owned (same as FlexLayout/INode).

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Content width range in cells at one available width (Spectre
/// <c>Measurement</c> analogue): <see cref="Min"/> is what the content needs,
/// <see cref="Max"/> is what it wants. Both count content cells only — chrome
/// (<see cref="Padding"/>) is applied separately by the parent.
/// Invariant: <c>0 &lt;= Min &lt;= Max</c> (factories normalize; instance
/// members clamp defensively, so a widget MUST also clamp — never trust the
/// input, same discipline as <see cref="Constraints"/>).
/// </summary>
public readonly record struct Measurement(int Min, int Max)
{
    /// <summary>Fixed-width content: min and max are both <paramref name="cells"/>.</summary>
    public static Measurement Exact(int cells)
    {
        int fit = Math.Max(0, cells);
        return new Measurement(fit, fit);
    }

    /// <summary>Ranged content, normalized to <c>0 &lt;= min &lt;= max</c>.</summary>
    public static Measurement Create(int min, int max)
    {
        int floor = Math.Max(0, min);
        return new Measurement(floor, Math.Max(floor, max));
    }

    /// <summary>Pins <paramref name="cells"/> into <c>Min..Max</c>.</summary>
    public int Clamp(int cells) => Math.Clamp(cells, Math.Max(0, Min), Math.Max(Math.Max(0, Min), Max));

    /// <summary>Shrinks both ends by <paramref name="horizontal"/> cells
    /// (the parent's padding bill); negative counts as zero.</summary>
    public Measurement Deflated(int horizontal)
    {
        int bill = Math.Max(0, horizontal);
        return Create(Math.Max(0, Min) - bill, Math.Max(Math.Max(0, Min), Max) - bill);
    }
}

/// <summary>
/// Cell insets around content (Spectre <c>IPaddable</c> + <c>Padder</c>
/// analogue): a border title row, a frame column, a status strip — every
/// chrome is four integers, and <see cref="Deflate"/> turns an outer rect
/// into the content rect the widget measures and paints within.
/// Negative insets count as zero everywhere (same rule as the solvers).
/// </summary>
public readonly record struct Padding(int Left, int Top, int Right, int Bottom)
{
    /// <summary>No chrome: <see cref="Deflate"/> is the identity.</summary>
    public static Padding Zero => new(0, 0, 0, 0);

    /// <summary>Same inset on all four sides, clamped at zero.</summary>
    public static Padding Uniform(int cells)
    {
        int fit = Math.Max(0, cells);
        return new Padding(fit, fit, fit, fit);
    }

    /// <summary>Horizontal/vertical insets, clamped at zero.</summary>
    public static Padding Symmetric(int horizontal, int vertical) =>
        new(Math.Max(0, horizontal), Math.Max(0, vertical), Math.Max(0, horizontal), Math.Max(0, vertical));

    /// <summary>Cells reserved on the width axis.</summary>
    public int Horizontal => Math.Max(0, Left) + Math.Max(0, Right);

    /// <summary>Cells reserved on the height axis.</summary>
    public int Vertical => Math.Max(0, Top) + Math.Max(0, Bottom);

    /// <summary>
    /// Content rect inside <paramref name="outer"/>: origin shifted by the
    /// left/top insets, extents shrunk by the axis totals and clamped at
    /// zero (an over-padded rect reads zero-size at the shifted origin —
    /// painters then paint nothing instead of writing into neighbours).
    /// </summary>
    public Rect Deflate(Rect outer) =>
        new(
            outer.X + Math.Max(0, Left),
            outer.Y + Math.Max(0, Top),
            Math.Max(0, outer.Width - Horizontal),
            Math.Max(0, outer.Height - Vertical));

    /// <summary>
    /// Outer frame around content at (<paramref name="x"/>,
    /// <paramref name="y"/>) of <paramref name="contentWidth"/> x
    /// <paramref name="contentHeight"/> cells: the inverse of
    /// <see cref="Deflate"/> for measuring a decorated widget outward.
    /// Extents clamp at zero; the origin is kept as-is.
    /// </summary>
    public Rect Frame(int x, int y, int contentWidth, int contentHeight) =>
        new(
            x - Math.Max(0, Left),
            y - Math.Max(0, Top),
            Math.Max(0, Math.Max(0, contentWidth) + Horizontal),
            Math.Max(0, Math.Max(0, contentHeight) + Vertical));
}

/// <summary>
/// Seats the Measure/Expand contract onto the existing solvers (Spectre
/// <c>IExpandable</c> policy): an expanding widget absorbs the remainder
/// with its measured minimum as the floor; a fixed widget takes exactly its
/// measured maximum and leaves the rest to the parent's justification.
/// </summary>
public static class WidgetMeasure
{
    /// <summary>
    /// Maps a widget's <paramref name="measurement"/> and
    /// <paramref name="expand"/> flag to the solver track the parent seats:
    /// <c>Min(floor)</c> for expanding content, <c>Fixed(fit)</c> otherwise.
    /// Pure layout math — no painting, no viewport, no diff.
    /// </summary>
    public static FlexTrack ToTrack(Measurement measurement, bool expand)
    {
        int floor = Math.Max(0, measurement.Min);
        int fit = Math.Max(floor, measurement.Max);
        return expand ? FlexTrack.Min(floor) : FlexTrack.Fixed(fit);
    }
}
