// TR2 (#1221, epic #1195): public size-constraint vocabulary —
// Fixed / Fill(+weight) / Auto / Percent over one axis.
// Engine-owned port of Termina's SizeConstraint (same four shapes, same
// Compute contract, same factory names); the solver half stays in
// FlexLayout/FlexTrack, reached through ToFlexTrack (Auto maps to
// FlexTrack.Auto, so intrinsic sizing survives the conversion).
// Same leaf rule as every file in this directory: BCL-only,
// AOT-compatible, zero Harbor references, render-thread owned.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// How one layout node claims space along one axis (Termina analogue:
/// every node carries a height and a width constraint; containers resolve
/// them in two passes — fixed/auto first, fill splits the remainder by
/// weight). Construct via <see cref="Exactly"/> / <see cref="FillRemaining"/>
/// / <see cref="AutoSize"/> / <see cref="Percentage"/>.
/// </summary>
public abstract record SizeConstraint
{
    /// <summary>
    /// Resolves the claim to cells. Negative inputs count as zero
    /// (same rule as the solvers).
    /// </summary>
    /// <param name="available">Total space on this axis.</param>
    /// <param name="contentSize">Measured content extent (auto-sizing only).</param>
    /// <param name="remaining">Space left after fixed/auto claims (fill only).</param>
    public abstract int Compute(int available, int contentSize, int remaining);

    /// <summary>Whether <see cref="Compute"/> reads <c>remaining</c>.</summary>
    public virtual bool NeedsRemaining => false;

    /// <summary>Whether <see cref="Compute"/> reads <c>contentSize</c>.</summary>
    public virtual bool NeedsContentSize => false;

    /// <summary>Converts this constraint to its solver track.</summary>
    public abstract FlexTrack ToFlexTrack();

    /// <summary>Exact cell count, clamped to fit.</summary>
    public sealed record Fixed(int Value) : SizeConstraint
    {
        /// <inheritdoc />
        public override int Compute(int available, int contentSize, int remaining) =>
            Math.Min(Math.Max(0, Value), Math.Max(0, available));

        /// <inheritdoc />
        public override FlexTrack ToFlexTrack() => FlexTrack.Fixed(Value);
    }

    /// <summary>Takes the remainder after fixed/auto claims are satisfied.</summary>
    public sealed record Fill : SizeConstraint
    {
        /// <summary>Proportional share when several fills compete (default 1).</summary>
        public int Weight { get; init; } = 1;

        /// <inheritdoc />
        public override bool NeedsRemaining => true;

        /// <inheritdoc />
        public override int Compute(int available, int contentSize, int remaining) =>
            Math.Max(0, remaining);

        /// <inheritdoc />
        public override FlexTrack ToFlexTrack() => FlexTrack.Fill(Weight);
    }

    /// <summary>Sizes to content within optional bounds.</summary>
    public sealed record Auto : SizeConstraint
    {
        /// <summary>Floor in cells (default 0).</summary>
        public int Min { get; init; }

        /// <summary>Cap in cells (default unbounded).</summary>
        public int Max { get; init; } = int.MaxValue;

        /// <inheritdoc />
        public override bool NeedsContentSize => true;

        /// <inheritdoc />
        public override int Compute(int available, int contentSize, int remaining)
        {
            int size = Math.Max(Math.Max(0, Min), Math.Max(0, contentSize));
            size = Math.Min(size, Math.Max(0, Max));
            return Math.Min(size, Math.Max(0, available));
        }

        /// <inheritdoc />
        public override FlexTrack ToFlexTrack() => FlexTrack.Auto(Min, Max);
    }

    /// <summary>Share of the available space, 0..100.</summary>
    public sealed record Percent(int Value) : SizeConstraint
    {
        /// <inheritdoc />
        public override int Compute(int available, int contentSize, int remaining) =>
            Math.Max(0, available) * Math.Clamp(Value, 0, 100) / 100;

        /// <inheritdoc />
        public override FlexTrack ToFlexTrack() => FlexTrack.Percent(Value / 100f);
    }

    /// <summary>Fixed size in cells (columns or rows).</summary>
    public static SizeConstraint Exactly(int value) => new Fixed(value);

    /// <summary>Fill the remainder; <paramref name="weight"/> splits it proportionally.</summary>
    public static SizeConstraint FillRemaining(int weight = 1) => new Fill { Weight = Math.Max(1, weight) };

    /// <summary>Size to content, clamped to <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static SizeConstraint AutoSize(int min = 0, int max = int.MaxValue)
    {
        int floor = Math.Max(0, min);
        return new Auto { Min = floor, Max = Math.Max(floor, max) };
    }

    /// <summary>Percentage of the available space, 0..100.</summary>
    public static SizeConstraint Percentage(int value) => new Percent(value);
}
