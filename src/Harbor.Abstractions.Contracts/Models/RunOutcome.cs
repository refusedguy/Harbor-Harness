using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Models;

/// <summary>
///     Why a run stopped (epic #41, slice B1: observable run).
/// </summary>
/// <remarks>
///     <para>
///         Tri-state, deliberately honest (B4 builds on this): <see cref="Stopped" />
///         (cancelled/aborted) is neither <see cref="Succeeded" /> nor
///         <see cref="Failed" />. An interrupted run must never be reported as failed.
///     </para>
/// </remarks>
public enum RunStopReason
{
    /// <summary>The run finished normally (terminal assistant message, no error, not cancelled).</summary>
    Succeeded,

    /// <summary>The run ended with an error (provider/stream failure surfaced via <c>AgentErrorEvent</c>, or terminal <c>StopReason.Error</c>).</summary>
    Failed,

    /// <summary>The run was cancelled or aborted (<c>AgentEndEvent.Cancelled</c>, or terminal <c>StopReason.Aborted</c>). Not a failure.</summary>
    Stopped,

    /// <summary>
    ///     The run was ended by a limit the user set — the step budget or the
    ///     wall-clock budget (<see cref="RunLimitKind" />). Not a failure and not a
    ///     cancellation: nothing malfunctioned, and nobody pressed stop. The work
    ///     is INCOMPLETE, which is what distinguishes this from
    ///     <see cref="Succeeded" /> and why it cannot be folded into
    ///     <see cref="Stopped" /> either.
    /// </summary>
    LimitExceeded,
}

/// <summary>
///     Which limit ended a run (epic #41, slice B2.2).
/// </summary>
/// <remarks>
///     <para>
///         A member of the execution axis's own second dimension, not a new axis:
///         "why did the run stop" is <see cref="RunStopReason" />, and "which of
///         the bounds did it hit" is this. They are asked together and answered
///         together, so a single <c>LimitExceeded</c> plus one of these says
///         everything the record needs, and a third limit later needs a member
///         here rather than a new enum.
///     </para>
///     <para>
///         This is deliberately NOT the same question as epic #406's
///         <c>Interrupted</c>. That one is "did the process reach a terminal event
///         at all" — an epistemic question about whether the fact is knowable. A
///         limit stop is a causal question about a run whose terminal event is
///         known exactly. A run stopped by the clock is fully
///         <c>Interrupted == false</c> and still <c>LimitExceeded</c>; answering
///         the two with one member is the #711 shape (two meanings in one field).
///     </para>
/// </remarks>
public enum RunLimitKind
{
    /// <summary>The wall-clock budget (<c>WorkspaceLimits.TimeoutSeconds</c>) elapsed.</summary>
    Timeout,

    /// <summary>The step budget (<c>AgentDefinition.MaxSteps</c>) was consumed.</summary>
    MaxSteps,
}

/// <summary>
///     One tool call linked to its execution result within a run.
/// </summary>
/// <remarks>
///     The linkage reuses the existing message format: <see cref="ToolCallPart.Id" />
///     matched against <see cref="ToolResultEntry.ToolCallId" /> — no parallel
///     truth system, no new storage shape (B1 rule).
/// </remarks>
/// <param name="ToolCallId">The tool call id (matches <see cref="ToolResultEntry.ToolCallId" />).</param>
/// <param name="ToolName">The name of the tool that was invoked.</param>
/// <param name="IsError">Whether the linked result represents an error (false when no result was recorded yet).</param>
public sealed record ToolCallLink(
    string ToolCallId,
    string ToolName,
    bool IsError);

/// <summary>
///     Stored outcome of one observable run (epic #41, slice B1).
/// </summary>
/// <remarks>
///     <para>
///         <b>Lifecycle mapping (zero new event types):</b> a run starts at
///         <c>AgentStartEvent</c>, progresses through <c>TurnStartEvent</c> /
///         <c>TurnEndEvent</c> pairs, and finishes at the terminal
///         <c>AgentEndEvent</c> (<c>Cancelled</c> maps to
///         <see cref="RunStopReason.Stopped" />) or <c>AgentErrorEvent</c>
///         (maps to <see cref="RunStopReason.Failed" />).
///     </para>
///     <para>
///         <b>Storage (no new table/file):</b> the outcome is a read-model
///         reconstructed from the messages already persisted in the existing
///         session store (<c>ISessionStore</c> message history) — one workspace,
///         one agent per run. Reconstruct via <see cref="Reconstruct" />
///         after the terminal event lands.
///     </para>
/// </remarks>
/// <param name="RunId">The run this outcome belongs to (minted at run start).</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="StopReason">Why the run stopped.</param>
/// <param name="StartedAt">Timestamp of the first message attributed to the run.</param>
/// <param name="FinishedAt">Timestamp of the last message attributed to the run.</param>
/// <param name="MessageIds">Ids of the run's messages in chronological order.</param>
/// <param name="ToolCalls">Tool calls issued by the run, each linked to its result.</param>
/// <param name="ErrorMessage">User-facing error text when <see cref="StopReason" /> is <see cref="RunStopReason.Failed" />; otherwise null.</param>
/// <param name="Limit">
///     Which limit ended the run, when <see cref="StopReason" /> is
///     <see cref="RunStopReason.LimitExceeded" />; otherwise null. Null is the
///     honest value for every other stop reason — a plausible constant here would
///     put a limit on runs that never hit one.
/// </param>
public sealed record RunOutcome(
    RunId RunId,
    string SessionId,
    RunStopReason StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> MessageIds,
    IReadOnlyList<ToolCallLink> ToolCalls,
    string? ErrorMessage = null,
    RunLimitKind? Limit = null)
{
    /// <summary>
    ///     Reconstruct a finished run's outcome from the session store's message
    ///     history plus the terminal lifecycle events. Pure function — no I/O.
    /// </summary>
    /// <param name="runId">The run id minted at run start.</param>
    /// <param name="sessionId">The owning session id.</param>
    /// <param name="messages">The run's messages in chronological order (e.g. from <c>ISessionStore.GetMessagesAsync</c>).</param>
    /// <param name="end">The terminal <c>AgentEndEvent</c> of the run.</param>
    /// <param name="error">The terminal <c>AgentErrorEvent</c>, when the run errored; otherwise null.</param>
    /// <returns>The reconstructed <see cref="RunOutcome" />.</returns>
    public static RunOutcome Reconstruct(
        RunId runId,
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        AgentEndEvent end,
        AgentErrorEvent? error = null) =>
        Reconstruct(runId, sessionId, messages, cancelled: end.Cancelled, errorMessage: error?.Message);

    /// <summary>
    ///     Reconstruct a finished run's outcome from stored messages and terminal
    ///     stop signals. Pure function — no I/O.
    /// </summary>
    /// <param name="runId">The run id minted at run start.</param>
    /// <param name="sessionId">The owning session id.</param>
    /// <param name="messages">The run's messages in chronological order (e.g. from <c>ISessionStore.GetMessagesAsync</c>).</param>
    /// <param name="cancelled">True when the run ended via cancellation (<c>AgentEndEvent.Cancelled</c>).</param>
    /// <param name="errorMessage">Error text when the run errored (<c>AgentErrorEvent.Message</c>); otherwise null.</param>
    /// <returns>The reconstructed <see cref="RunOutcome" />.</returns>
    public static RunOutcome Reconstruct(
        RunId runId,
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        bool cancelled = false,
        string? errorMessage = null)
    {
        var resultByCallId = new Dictionary<string, ToolResultEntry>(StringComparer.Ordinal);
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is ToolResultMessage toolResults)
            {
                for (int j = 0; j < toolResults.Results.Count; j++)
                    resultByCallId[toolResults.Results[j].ToolCallId] = toolResults.Results[j];
            }
        }

        var messageIds = new List<string>(messages.Count);
        var toolCalls = new List<ToolCallLink>(capacity: 4);
        AssistantMessage? lastAssistant = null;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset finishedAt = startedAt;

        for (int i = 0; i < messages.Count; i++)
        {
            AgentMessage message = messages[i];
            messageIds.Add(message.Id);

            if (i == 0 || message.CreatedAt < startedAt)
                startedAt = message.CreatedAt;
            if (i == 0 || message.CreatedAt > finishedAt)
                finishedAt = message.CreatedAt;

            if (message is AssistantMessage assistant)
            {
                lastAssistant = assistant;
                for (int p = 0; p < assistant.Parts.Count; p++)
                {
                    if (assistant.Parts[p] is ToolCallPart call)
                    {
                        bool isError = resultByCallId.TryGetValue(call.Id, out ToolResultEntry? entry) && entry.IsError;
                        toolCalls.Add(new ToolCallLink(call.Id, call.ToolName, isError));
                    }
                }
            }
        }

        RunStopReason stopReason;
        if (cancelled)
            stopReason = RunStopReason.Stopped;
        else if (errorMessage is not null)
            stopReason = RunStopReason.Failed;
        else if (lastAssistant is not null && lastAssistant.StopReason == global::Harbor.Abstractions.Models.StopReason.Aborted)
            stopReason = RunStopReason.Stopped;
        else if (lastAssistant is not null && lastAssistant.StopReason == global::Harbor.Abstractions.Models.StopReason.Error)
            stopReason = RunStopReason.Failed;
        else
            stopReason = RunStopReason.Succeeded;

        return new RunOutcome(
            runId,
            sessionId,
            stopReason,
            startedAt,
            finishedAt,
            messageIds,
            toolCalls,
            stopReason == RunStopReason.Failed ? errorMessage : null);
    }
}
