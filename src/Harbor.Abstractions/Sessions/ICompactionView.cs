using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     History-shaping view over a session history: the pure truncation and
///     compaction-fold rules behind an interface, so the agent turn pipeline
///     depends on a contract instead of statics (#1612).
/// </summary>
/// <remarks>
///     <para>
///         The default implementation is <c>CompactionView</c> in
///         <c>Harbor.Application.Sessions</c>: a stateless forwarder over the
///         <c>CompactionPolicy</c> pure statics. The methods stay pure and
///         static there; this interface is the seam the turn pipeline
///         (<c>TurnRunner</c>, <c>CompactionBehavior</c>) is composed against,
///         and the step that lets a later slice break the
///         <c>Agents → Sessions</c> reference without touching either caller.
///     </para>
/// </remarks>
public interface ICompactionView
{
    /// <summary>
    ///     Default token reserve used by the truncation methods when the caller does not
    ///     supply one.
    /// </summary>
    public const int DefaultReserveTokens = 16384;

    /// <summary>
    ///     Keep only the most recent messages that fit the model's context budget
    ///     (<c>ContextWindow − reserve − MaxOutputTokens</c>), dropping the older
    ///     middle/head. The kept slice never opens on an orphan
    ///     <c>ToolResultMessage</c>, and at least one message is always kept.
    ///     Returns the history unchanged when it already fits.
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The target model (context window + output budget).</param>
    /// <param name="tokenTracker">Token estimator used to size the kept tail.</param>
    /// <param name="reserveTokens">Safety reserve below the context window.</param>
    /// <returns>A new list with the kept tail messages, or <paramref name="messages" /> when it is empty.</returns>
    public IReadOnlyList<AgentMessage> TruncateToFit(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = DefaultReserveTokens);

    /// <summary>
    ///     Strict-reduction variant of <see cref="TruncateToFit" />: the
    ///     compaction-failure fallback. Unlike the budget-fit walk — which keeps
    ///     everything when the history already fits — this ALWAYS drops part of
    ///     the head once the history exceeds a small floor, because after a
    ///     failed LLM compaction the next request must actually shrink.
    /// </summary>
    /// <param name="messages">The current message history.</param>
    /// <param name="model">The target model (context window + output budget).</param>
    /// <param name="tokenTracker">Token estimator used to size the budget-fit ceiling.</param>
    /// <param name="reserveTokens">Safety reserve below the context window.</param>
    /// <returns>A new list holding strictly fewer messages than the input whenever the input exceeds the keep floor.</returns>
    public IReadOnlyList<AgentMessage> TruncateToFitStrict(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = DefaultReserveTokens);

    /// <summary>
    ///     Materialize the effective post-compaction history from a raw session
    ///     history that contains compaction summaries: <c>[summary] + tail-from-anchor
    ///     + messages-appended-after-the-summary</c>. Returns the input instance
    ///     unchanged when no summary exists or the anchor cannot be resolved.
    /// </summary>
    /// <param name="messages">The raw (append-only) session history.</param>
    /// <returns>The compacted view, or <paramref name="messages" /> when nothing is compacted.</returns>
    public IReadOnlyList<AgentMessage> MaterializeCompactedView(IReadOnlyList<AgentMessage> messages);
}
