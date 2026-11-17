namespace Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     Shared cell-diff tuning: hints covering more than
///     <see cref="HintAreaThreshold" /> of the screen fall back to the full
///     scan. Canonical home for the portable encoder
///     (<see cref="RowHashDiffEncoder" />). The ANSI engine (<c>DiffEngine</c>)
///     is a BCL-only leaf that cannot reference this layer (#436), so it reads
///     the same number from its own <c>CellDamageContract</c>; the two are held
///     equal by <c>CellForgeDiffEncoderFactoryTests.Threshold_Single_Source_Of_Truth</c>
///     rather than by a hand-maintained mirror (#2220).
/// </summary>
public static class CellDiffHints
{
    /// <summary>Hints above this share of the screen fall back to full scan.</summary>
    public const double HintAreaThreshold = 0.25;
}
