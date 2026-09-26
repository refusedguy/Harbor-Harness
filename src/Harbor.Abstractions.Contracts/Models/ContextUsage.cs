namespace Harbor.Abstractions.Models;

/// <summary>
///     Canonical context-usage math (issue #75) — the single definition of
///     "ctx%" shared by every surface: the legacy status-bar view model,
///     the CellForge sidebar, and the CellForge status bar.
/// </summary>
/// <remarks>
///     <para>
///         <b>Definition:</b> <c>used = accumulated input + accumulated output
///         tokens</c> over the model's <c>ContextWindow</c>, clamped to
///         0…100%. This is a spend-vs-window proxy, <b>not</b> a live
///         window-fill gauge: the only per-step accurate fill figure (last
///         step's <c>Usage.InputTokens</c>, i.e. the prompt just sent) is
///         available solely on the event-driven path, while every pull-feed
///         surface (<c>ITokenTracker.GetStats()</c>,
///         <c>SessionStatsEvent.Metadata</c>, <c>CostSnapshot</c>) carries
///         only cumulative totals. Accumulated in+out is therefore the only
///         formula derivable from existing tracker state at <i>all</i> call
///         sites — and it matches the cumulative totals printed next to the
///         percentage on the status line (previously the percent was
///         last-step-only while its neighbors accumulated — the #75 bug).
///     </para>
///     <para>
///         <b>Saturation caveat:</b> cumulative spend grows unboundedly, so
///         long sessions pin at 100%. That is intended: the number answers
///         "how much window-equivalent have we spent", consistent with the
///         adjacent <c>↑/↓</c> totals. A true next-input projection ("will
///         the <i>next</i> request fit?") needs per-message estimates plus
///         the pending input text — neither is reachable from the
///         Presentation layer (<c>HeuristicTokenEstimator</c> lives in
///         Application) — so projection is explicitly out of scope.
///     </para>
///     <para>
///         Pure BCL, allocation-free, AOT-safe. Thresholds double as the
///         CellForge bar color bands (warn ≥ 50%, danger ≥ 85%); text
///         surfaces render the raw percent with no bands.
///     </para>
/// </remarks>
public static class ContextUsage
{
    /// <summary>Warn band: usage at or above 50% of the context window.</summary>
    public const double WarnThreshold = 0.50;

    /// <summary>Danger band: usage at or above 85% of the context window.</summary>
    public const double DangerThreshold = 0.85;

    /// <summary>
    ///     Context usage percent from accumulated token counters.
    ///     Returns 0 when <paramref name="contextWindow" /> is unknown
    ///     (≤ 0); saturates at 100.
    /// </summary>
    public static int PercentUsed(long tokensIn, long tokensOut, long contextWindow) =>
        PercentUsed(tokensIn + tokensOut, contextWindow);

    /// <summary>
    ///     Context usage percent from an already-summed token count.
    ///     Callers must pass accumulated input+output (the #75 canonical
    ///     "used" definition) — not a single step's count.
    /// </summary>
    public static int PercentUsed(long usedTokens, long contextWindow)
    {
        if (contextWindow <= 0 || usedTokens <= 0)
        {
            return 0;
        }

        if (usedTokens >= contextWindow)
        {
            return 100;
        }

        if (usedTokens > long.MaxValue / 100)
        {
            return 100;
        }

        return (int)(usedTokens * 100 / contextWindow);
    }

    /// <summary>
    ///     Context usage ratio (0…1) from accumulated token counters.
    ///     Returns 0 when <paramref name="contextWindow" /> is unknown
    ///     (≤ 0); saturates at 1.
    /// </summary>
    public static double RatioUsed(long tokensIn, long tokensOut, long contextWindow) =>
        RatioUsed(tokensIn + tokensOut, contextWindow);

    /// <summary>
    ///     Context usage ratio (0…1) from an already-summed token count.
    ///     See <see cref="PercentUsed(long,long)" /> for the "used" definition.
    /// </summary>
    public static double RatioUsed(long usedTokens, long contextWindow)
    {
        if (contextWindow <= 0 || usedTokens <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)usedTokens / contextWindow, 0, 1);
    }
}
