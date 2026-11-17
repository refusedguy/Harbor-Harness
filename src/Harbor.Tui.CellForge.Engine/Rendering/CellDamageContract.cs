namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     The single damage policy shared by the engine frame pipeline (#2220):
///     one threshold, one decision, one place to count which branch a frame
///     took. Both <see cref="DiffEngine"/> (ANSI engine) and the portable
///     encoder's <c>CellDiffHints</c> are held to the same number by
///     <c>CellForgeDiffEncoderFactoryTests.Threshold_Single_Source_Of_Truth</c>;
///     the engine cannot reference the shared rendering layer (BCL-only leaf,
///     #436), so the guard is the mechanical link between the two vocabularies.
/// </summary>
public static class CellDamageContract
{
    /// <summary>
    ///     Canonical hint-area fallback threshold: hints covering at least this
    ///     share of the screen fall back to the full scan. The engine reads this
    ///     value instead of carrying a hand-mirrored copy of the number.
    /// </summary>
    public const double HintAreaThreshold = 0.25;

    /// <summary>
    ///     The one hinted-scan vs full-scan decision. Strictly-less-than keeps
    ///     the exact golden boundary: an area equal to 25% still falls back.
    ///     A clean or zero-sized screen never qualifies for the hinted scan.
    /// </summary>
    public static bool ShouldUseHints(long hintArea, long screenArea) =>
        screenArea > 0 && hintArea >= 0 && hintArea < screenArea * HintAreaThreshold;
}
