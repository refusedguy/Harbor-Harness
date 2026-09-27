namespace Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     Shared cell-diff tuning: hints covering more than
///     <see cref="HintAreaThreshold" /> of the screen fall back to the full
///     scan. Single home for the threshold so the ANSI engine
///     (<c>DiffEngine</c>) and the portable encoder
///     (<see cref="RowHashDiffEncoder" />) cannot drift apart.
/// </summary>
public static class CellDiffHints
{
    /// <summary>Hints above this share of the screen fall back to full scan.</summary>
    public const double HintAreaThreshold = 0.25;
}
