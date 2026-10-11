// TR1 (#1220, epic #1195): generic composition node contract —
// Measure/Arrange/Paint three-phase nodes over the engine-owned vocabulary
// (Rect, Size, SplitDir, ScreenBuffer). BCL-only, AOT-compatible, zero Harbor
// references (same leaf rule as every file in this directory).
// Render-thread owned, not thread-safe — same as FlexLayout and DiffEngine.
// The chat layer (Chat/Rendering/LayoutTree.cs) is untouched: its
// collapse-priority solver is a read-only sample, never a dependency.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Measurement bounds for <see cref="INode.Measure"/>. Both extents are hard
/// upper bounds; negative counts as zero (parents clamp defensively, so a
/// node MUST also clamp — never trust the input).
/// There is no unbounded axis: flexibility is declared with
/// <see cref="FlexTrack"/> (<c>Fill</c> / <c>Min</c> / <c>Max</c>) and
/// resolved by the parent's arrange phase, never by measuring into infinity.
/// </summary>
public readonly record struct Constraints(int MaxWidth, int MaxHeight)
{
    /// <summary>Usable width bound (clamped at zero).</summary>
    public int Width => Math.Max(0, MaxWidth);

    /// <summary>Usable height bound (clamped at zero).</summary>
    public int Height => Math.Max(0, MaxHeight);
}

/// <summary>
/// Composable layout node: three phases, always in order, on the render
/// thread only (ConsoleEx <c>IDOMPaintable</c> / Termina measure-render
/// lesson, chat-agnostic):
/// <list type="number">
///   <item><description><see cref="Measure"/> — report the desired
///     <see cref="Size"/> within bounds. Pure: no buffer writes, no state
///     mutations (same discipline as <c>docs/GENERIC_LAYOUT.md</c> §4 Build
///     purity).</description></item>
///   <item><description><see cref="Arrange"/> — accept the final
///     <see cref="Rect"/> from the parent; containers position children via
///     the pure solvers (<see cref="FlexLayout"/>, <see cref="StackLayout"/>).
///     No painting.</description></item>
///   <item><description><see cref="Paint"/> — paint within
///     <see cref="Arranged"/> clipped to the clip. Reads
///     state, writes cells, mutates nothing else.</description></item>
/// </list>
/// </summary>
public interface INode
{
    /// <summary>Desired size within <paramref name="constraints"/>. Pure.</summary>
    Size Measure(Constraints constraints);

    /// <summary>Accepts the final rect; containers arrange children here.</summary>
    /// <param name="rect">Assigned area. Negative extents count as zero.</param>
    void Arrange(Rect rect);

    /// <summary>Paints within <see cref="Arranged"/> clipped to the clip.</summary>
    /// <param name="buffer">Target grid. Never null.</param>
    /// <param name="clip">Paint clip. The node MUST NOT write outside the
    /// intersection of <see cref="Arranged"/> and <paramref name="clip"/>.</param>
    void Paint(ScreenBuffer buffer, Rect clip);

    /// <summary>Rect assigned by the last <see cref="Arrange"/>.</summary>
    Rect Arranged { get; }
}

/// <summary>
/// Childless node base: <see cref="Arrange"/> stores the rect, subclasses
/// implement <see cref="Measure"/> and <see cref="Paint"/> only.
/// </summary>
public abstract class LeafNode : INode
{
    /// <inheritdoc />
    public Rect Arranged { get; private set; }

    /// <inheritdoc />
    public abstract Size Measure(Constraints constraints);

    /// <inheritdoc />
    public virtual void Arrange(Rect rect) => Arranged = Normalize(rect);

    /// <inheritdoc />
    public abstract void Paint(ScreenBuffer buffer, Rect clip);

    /// <summary>Negative extents count as zero (same rule as the solvers).</summary>
    protected static Rect Normalize(Rect rect) =>
        new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));

    /// <summary>Paint-area guard: intersection of the arranged rect and the clip.</summary>
    protected Rect PaintArea(Rect clip) => Arranged.Intersect(clip);
}
