namespace Harbor.Abstractions.Models;

/// <summary>
///     Canonical context-occupancy math (issue #75) — the single definition of
///     "ctx%" shared by every surface: the legacy status-bar view model,
///     the CellForge sidebar, and the CellForge status bar.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the caller must pass.</b> This class is pure arithmetic: it
///         takes an already-measured "used" token count and a window and returns
///         a clamped percentage. It does not and cannot know how the caller
///         measured. Occupancy — how much of the window the request just sent
///         filled — is <c>StepFinishEvent.Usage.InputTokens</c>. Session-cumulative
///         spend (<c>SessionMetadata.AddUsage</c> accumulates across the whole
///         session, as do <c>ITokenTracker.GetStats()</c>,
///         <c>SessionStatsEvent.Metadata</c> and <c>CostSnapshot</c>) is a
///         different quantity: it grows without bound, so fed in here it pins the
///         reading at 100% by turn 4 on a 128k window while the payload never
///         grew. That is bug #623, not a rounding nuance.
///     </para>
///     <para>
///         <b>Callers:</b> the CellForge ctx bar (#630) and the legacy
///         <c>StatusBarViewModel</c> ctx cell (#641) both pass the last
///         request's prompt tokens. The CellForge sidebar still passes
///         cumulative <c>TokensIn/TokensOut</c> — a known divergence, tracked
///         separately; the arithmetic here is right for whatever it is handed,
///         so the fix belongs at the call site, not in this class.
///     </para>
///     <para>
///         <b>Unknown ≠ zero.</b> A window of 0 (the model published no context
///         length) and a "used" of 0 both return 0, which is also the correct
///         answer for "no request has been sent yet" — a surface that must stay
///         dark in that case should track the presence of a reading itself
///         (<c>StatusViewModel.TryGetContextTokens</c>), not encode absence in
///         the number.
///     </para>
///     <para>
///         A true next-input projection ("will the <i>next</i> request fit?")
///         needs per-message estimates plus the pending input text — neither is
///         reachable from the Presentation layer
///         (<c>HeuristicTokenEstimator</c> lives in Application) — so projection
///         is explicitly out of scope.
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
    ///     Context occupancy percent from a pair of token counters, summed.
    ///     Returns 0 when <paramref name="contextWindow" /> is unknown
    ///     (≤ 0); saturates at 100. What the two counters <em>mean</em> — one
    ///     request's prompt tokens, or the session's running totals — is the
    ///     caller's decision; see the class remarks on #623.
    /// </summary>
    public static int PercentUsed(long tokensIn, long tokensOut, long contextWindow) =>
        PercentUsed(tokensIn + tokensOut, contextWindow);

    /// <summary>
    ///     Context occupancy percent from an already-summed token count.
    ///     Callers on a ctx segment pass the prompt tokens of the request the
    ///     provider just accepted (<c>StepFinishEvent.Usage.InputTokens</c>);
    ///     passing session-cumulative spend instead is bug #623.
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
    ///     Context occupancy ratio (0…1) from a pair of token counters, summed.
    ///     Returns 0 when <paramref name="contextWindow" /> is unknown
    ///     (≤ 0); saturates at 1. See <see cref="PercentUsed(long,long,long)" />
    ///     for what the counters may mean.
    /// </summary>
    public static double RatioUsed(long tokensIn, long tokensOut, long contextWindow) =>
        RatioUsed(tokensIn + tokensOut, contextWindow);

    /// <summary>
    ///     The input-token figure a status cell shows — ONE definition for every
    ///     surface (#651), because the two numbers it chooses between answer
    ///     different questions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Paid</b> — <paramref name="sessionTotalInputTokens" /> — is the
    ///         sum of every request's full input. It is the bill: an uncached
    ///         provider reads and charges the whole prompt again on every turn, so
    ///         on turn N this is roughly N × the context. Nothing should ever hide
    ///         it from a price.
    ///     </para>
    ///     <para>
    ///         <b>Occupied</b> — <paramref name="contextTokens" /> — is the prompt
    ///         size of the request the provider last accepted: what the window
    ///         holds right now, and the same figure the ctx bar reads (#630). It is
    ///         what a person asking "how full is my context" means, and it does
    ///         not grow with the turn count.
    ///     </para>
    ///     <para>
    ///         The fallback is the degradation, not the rule: a session that
    ///         published totals but has run no request in this process has no
    ///         request size to report (a message history records tokens, never the
    ///         shape of a request), so the cell shows the total it was given
    ///         rather than claiming an empty context. Every turn after that
    ///         publishes a request and the cell switches to the occupied figure.
    ///     </para>
    /// </remarks>
    /// <param name="contextTokens">
    ///     Prompt tokens of the most recent request; 0 when none was observed.
    /// </param>
    /// <param name="sessionTotalInputTokens">Paid total, as the core published it.</param>
    /// <returns>The occupied figure when known, else the paid total.</returns>
    public static long DisplayedInputTokens(long contextTokens, long sessionTotalInputTokens) =>
        contextTokens > 0 ? contextTokens : sessionTotalInputTokens;

    /// <summary>
    ///     Context occupancy ratio (0…1) from an already-summed token count.
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
