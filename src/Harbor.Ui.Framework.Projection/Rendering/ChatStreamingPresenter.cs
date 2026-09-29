using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Rendering;

/// <summary>
///     Derives session status and streaming-presentation state from a
///     <see cref="UiState" /> snapshot. Registered as a singleton in
///     <c>AppHost</c>.
/// </summary>
public sealed class ChatStreamingPresenter
{
    /// <summary>
    ///     The session's status, as <see cref="ChatDomainState.SessionStatus" />
    ///     holds it. Registered as a singleton in <c>AppHost</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#687 — this is a READ, not a decision.</b> The status used to be
    ///         re-derived here, on a heuristic over the transcript: running →
    ///         <see cref="SessionStatus.Working" />, a status string of
    ///         <c>"error"</c> → <see cref="SessionStatus.Error" />, and a
    ///         transcript whose LAST line was an assistant line →
    ///         <see cref="SessionStatus.Done" />.
    ///     </para>
    ///     <para>
    ///         That last arm was a second opinion about a run the core already
    ///         knows the end of, and it did not have to agree with the core's own
    ///         answer. A run that ended in <c>AgentErrorEvent</c> can still be
    ///         sitting on an assistant line, so the heuristic repainted failed
    ///         runs green. It also competed with the writers —
    ///         <c>SessionLifecycleService</c> sets
    ///         <see cref="SessionStatus.Error" /> when a branch cannot be opened
    ///         and <c>SubAgentRunner</c> stamps Working/Done/Error around a real
    ///         sub-agent run — and <c>ChatViewModel.RenderFrameTick</c> overwrote
    ///         both on the next 16 ms frame.
    ///     </para>
    ///     <para>
    ///         The decision now happens once, on the transition that establishes
    ///         it, in <see cref="ChatAppReducer" />: the core publishes
    ///         <c>AgentErrorEvent</c> for a failure,
    ///         <c>AgentEndEvent(Cancelled: true)</c> for an abort, and a clean
    ///         <c>AgentEndEvent</c> for a finish. The projection reads that.
    ///         <c>SessionStatusSourceRule</c> in
    ///         <c>tests/Harbor.Architecture.Tests/</c> is the guard.
    ///     </para>
    /// </remarks>
    /// <param name="state">The current UiState.</param>
    /// <returns>The status the reducer decided for the active session.</returns>
    public SessionStatus DeriveStatus(UiState state) => state.Chat.SessionStatus;
}
