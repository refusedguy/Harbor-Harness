using System.Globalization;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     The single formatting rule for the numeric status-bar cells
///     (<see cref="CostSnapshot" /> tokens + cost) — issue #488.
/// </summary>
/// <remarks>
///     <para>
///         Two surfaces render the same session numbers: the
///         <c>UiStatusBarModel</c> projection (Avalonia / Termina /
///         TerminalGui / SpectreTui) and the CellForge <c>StatusSeg</c> footer
///         row. They each formatted cost on their own — one emitted a bare,
///         unconditional four-decimal number, the other a <c>$</c>-prefixed value
///         hidden at zero — so one session rendered two different cost cells
///         depending on which host painted it (#457 shipped exactly that class
///         of bug). Both surfaces now read the cells built by
///         <c>StatusBarFacts</c> in the projection layer, which calls into this
///         type; nothing else may format these two values. A regression guard
///         counts the cost format string across the whole status path and fails
///         if it ever appears twice.
///     </para>
///     <para>
///         <b>Why this assembly:</b> <c>Harbor.Ui.Framework.State</c> is the
///         only project both the projection layer
///         (<c>Harbor.Ui.Framework.Projection</c>) and the view-model layer
///         (<c>StatusMappers</c>, which the XAML <c>CostToUsdConverter</c> /
///         <c>TokensToCompact</c> adapters wrap) already reference. Putting the
///         rule here keeps it a single implementation without adding a
///         <c>ProjectReference</c> the layer matrix in
///         <c>docs/ARCHITECTURE_LAYERS.md</c> §2 forbids.
///     </para>
/// </remarks>
public static class StatusBarText
{
    /// <summary>
    ///     Compact token count for a status cell ("1.2K" / "1.4M"); raw below a
    ///     thousand, "0" for zero/negative.
    /// </summary>
    public static string TokensToCompact(long tokens)
    {
        if (tokens <= 0) return "0";
        if (tokens < 1000) return tokens.ToString();
        if (tokens < 1_000_000) return $"{(tokens / 1000.0).ToString("F1", CultureInfo.InvariantCulture)}K";
        return $"{(tokens / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)}M";
    }

    /// <summary>
    ///     The "<c>in↑ out↓</c>" cell, or <see langword="null" /> when the session
    ///     has moved no tokens (no data ⇒ no cell, never a bare "0↑ 0↓").
    /// </summary>
    public static string? TokensCell(long tokensIn, long tokensOut) =>
        tokensIn > 0 || tokensOut > 0
            ? TokensToCompact(tokensIn) + "↑ " + TokensToCompact(tokensOut) + "↓"
            : null;

    /// <summary>
    ///     A USD cost as a fixed four-decimal string ("$0.0123"). Negative input
    ///     clamps to zero. The <c>$</c> is written literally rather than taken
    ///     from <c>"C4"</c> so the cell never renders a culture currency symbol
    ///     (<c>¤</c>) on a non-en-US host.
    /// </summary>
    public static string CostToUsd(decimal costUsd) =>
        "$" + (costUsd < 0 ? 0m : costUsd).ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    ///     The cost cell, or <see langword="null" /> when nothing was spent —
    ///     grok None-semantics (#457): a zero/negative cost hides the cell rather
    ///     than painting a meaningless "$0.0000" next to a live transcript.
    /// </summary>
    /// <param name="costUsd">Cumulative cost, as the core priced it.</param>
    /// <param name="costKnown">
    ///     <see langword="false" /> when the core could not price the session
    ///     (<c>Pricing.Unknown</c> — a local Ollama, or a paid provider whose
    ///     catalogue entry carries no rates). Then the cell reads "—": "we track
    ///     cost, this model's price is unknown" is a fact, and a "$0.0000" or a
    ///     missing cell would both be read as "free" (#653).
    /// </param>
    public static string? CostCell(decimal costUsd, bool costKnown = true)
    {
        if (!costKnown)
        {
            return UnknownCostCell;
        }

        return costUsd > 0 ? CostToUsd(costUsd) : null;
    }

    /// <summary>
    ///     The cell shown when the model publishes no price. An em dash, not a
    ///     zero and not silence: it is the one glyph that cannot be misread as an
    ///     amount.
    /// </summary>
    public const string UnknownCostCell = "—";
}
