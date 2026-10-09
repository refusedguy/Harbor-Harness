namespace Harbor.Abstractions.Models;

/// <summary>
///     Which origin a budget figure comes from (epic #41, slice B2.3).
/// </summary>
/// <remarks>
///     Four members, deliberately, and no <c>Default</c>/<c>Fallback</c>/
///     <c>Zero</c>: missing data is <see cref="Unknown" />, and absence is
///     not a fallback (#711 — two meanings in one field). A figure whose
///     source is unknown reads as unknown, never as zero: zero is also what
///     a genuinely free model reports, and the two must not share a value.
/// </remarks>
public enum BudgetSource
{
    /// <summary>Usage as returned by the API (<c>StepFinishEvent.Usage</c>).</summary>
    ProviderReported,

    /// <summary>The heuristic estimator (<c>HeuristicTokenEstimator</c>).</summary>
    LocalEstimate,

    /// <summary>Cost computed from a price table applied to reported usage.</summary>
    TariffCost,

    /// <summary>No data. Absence, not a fallback and not zero.</summary>
    Unknown,
}

/// <summary>
///     Why the recorded spend exceeds the cap that tripped (epic #41, slice B2.3).
/// </summary>
/// <remarks>
///     Any cap that claims to be exact is lying: usage arrives after the
///     request that spent it, parallel requests are in flight when the cap
///     trips, and cancellation lags. The overshoot is therefore modeled,
///     not hidden — the recorded actual may exceed the cap, and this says
///     which lag explains the excess.
/// </remarks>
public enum BudgetOvershootReason
{
    /// <summary>Recorded spend is within caps. The default; not a trip.</summary>
    None,

    /// <summary>Usage arrived after the request that spent it (the sequential-loop shape).</summary>
    LateUsage,

    /// <summary>Parallel requests were in flight when the cap tripped; they completed anyway.</summary>
    InFlightRequests,

    /// <summary>Spend recorded after the trip: the stop decision lagged behind the meter.</summary>
    CancelLag,
}

/// <summary>
///     Hard spend/size ceilings for one run (epic #41, slice B2.3).
/// </summary>
/// <remarks>
///     Every member is nullable; null means unbounded on that axis. The caps
///     are enforced at the same safe boundary as timeout/cancel (the turn
///     boundary in <c>AgentLoop</c>), and a hit terminates the run with the
///     matching <c>RunLimitKind</c> member — the #403/#1011 terminal shape,
///     reused rather than re-invented.
/// </remarks>
/// <param name="MaxTokens">Max reported-or-estimated tokens for the run; null is unbounded.</param>
/// <param name="MaxCostUsd">Max tariff cost in USD for the run; null is unbounded.</param>
/// <param name="MaxOutputBytes">Max streamed output bytes for the run; null is unbounded.</param>
public sealed record RunBudgetCaps(
    int? MaxTokens = null,
    decimal? MaxCostUsd = null,
    long? MaxOutputBytes = null);

/// <summary>
///     One budget read-model per run (epic #41, slice B2.3).
/// </summary>
/// <remarks>
///     <para>
///         The four sources are four distinctly named members. No member is a
///         blend: <see cref="ReportedUsage" /> is provider-reported usage or
///         null, <see cref="EstimatedTokens" /> is the heuristic figure,
///         <see cref="TariffCostUsd" /> is the price table applied to reported
///         usage (null when usage is missing), and <see cref="EstimatedCostUsd" />
///         is estimate-side money under its own name — it must never be
///         presented as billed cost (see <see cref="RunBudgetLabels" />).
///     </para>
///     <para>
///         Which source a reader gets is answered by <see cref="TokenSource" />
///         and <see cref="CostSource" />, never by a separate flag: a field
///         that reads a constant is a plausible value, not a fact (#591).
///     </para>
/// </remarks>
/// <param name="ReportedUsage">Usage the provider reported, summed over the run; null when nothing was reported.</param>
/// <param name="EstimatedTokens">Heuristic token estimate, summed over the run.</param>
/// <param name="TariffCostUsd">Price table × reported usage; null when usage is missing (Unknown, not zero).</param>
/// <param name="EstimatedCostUsd">Estimate-side money under its own name; never the billed figure.</param>
/// <param name="OutputBytes">Streamed output bytes recorded on the delta path.</param>
public sealed record RunBudget(
    Usage? ReportedUsage,
    int EstimatedTokens,
    decimal? TariffCostUsd,
    decimal? EstimatedCostUsd,
    long OutputBytes)
{
    /// <summary>
    ///     Which source the run's token figure comes from: reported usage
    ///     when any was reported, the heuristic estimate when one exists,
    ///     <see cref="BudgetSource.Unknown" /> otherwise.
    /// </summary>
    public BudgetSource TokenSource
    {
        get
        {
            if (ReportedUsage is not null)
                return BudgetSource.ProviderReported;
            if (EstimatedTokens > 0)
                return BudgetSource.LocalEstimate;
            return BudgetSource.Unknown;
        }
    }

    /// <summary>
    ///     Which source the run's money figure comes from: the tariff when
    ///     one was computed, the estimate-side figure when only an estimate
    ///     exists, <see cref="BudgetSource.Unknown" /> otherwise. The middle
    ///     member is <see cref="BudgetSource.LocalEstimate" /> on purpose —
    ///     estimate money is estimate-flavored, never billed.
    /// </summary>
    public BudgetSource CostSource
    {
        get
        {
            if (TariffCostUsd is not null)
                return BudgetSource.TariffCost;
            if (EstimatedCostUsd is not null)
                return BudgetSource.LocalEstimate;
            return BudgetSource.Unknown;
        }
    }
}

/// <summary>
///     Display labels for the two money figures of <see cref="RunBudget" />.
/// </summary>
/// <remarks>
///     The two constants exist so no UI or tool output can present
///     <c>EstimatedCostUsd</c> under the billed name by accident: the labels
///     are spelled differently on purpose, and the billed one contains no
///     form of "estimate". Presentation adoption is follow-up; the pin that
///     the names cannot be merged lives in the #404 guard tests.
/// </remarks>
public static class RunBudgetLabels
{
    /// <summary>Label for the billed figure (<c>TariffCostUsd</c>).</summary>
    public const string BilledCost = "billed cost";

    /// <summary>Label for estimate-side money (<c>EstimatedCostUsd</c>). Never used for the billed figure.</summary>
    public const string EstimatedCost = "estimated cost (not billed)";
}
