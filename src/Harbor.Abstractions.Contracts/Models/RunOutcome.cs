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
///     <para>
///         #993: a run that left no evidence of finishing is <see cref="Stopped" />,
///         not <see cref="Succeeded" />. The mapping used to end in a catch-all, so
///         an empty message history — a run that never started, or one whose
///         persistence produced nothing — was reported as "the loop exited cleanly".
///         <see cref="Succeeded" /> is a claim that a run completed; only a terminal
///         assistant message can support it.
///     </para>
/// </remarks>
public enum RunStopReason
{
    /// <summary>The run finished normally (terminal assistant message, no error, not cancelled).</summary>
    Succeeded,

    /// <summary>The run ended with an error (provider/stream failure surfaced via <c>AgentErrorEvent</c>, or terminal <c>StopReason.Error</c>).</summary>
    Failed,

    /// <summary>
    ///     The run was cancelled or aborted (<c>AgentEndEvent.Cancelled</c>, or terminal <c>StopReason.Aborted</c>). Not a failure.
    /// </summary>
    /// <remarks>
    ///     #993 also routes here the run that left no assistant message at all: with no
    ///     terminal assistant turn there is no evidence the loop exited cleanly, so the
    ///     honest member is the one that asserts no verdict on the work. A known
    ///     cancellation or a known error still wins over it — only the *absence* of
    ///     evidence falls through to this member.
    /// </remarks>
    Stopped,
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
/// <param name="StartedAt">
///     Timestamp of the first message attributed to the run; <c>null</c> when the run
///     left no message to date it. #993: this is honestly unknown, never defaulted to
///     <c>UtcNow</c> — an invented timestamp is a plausible fact standing in for a
///     missing one, and it is what made a run that never happened look like one that
///     took zero milliseconds.
/// </param>
/// <param name="FinishedAt">Timestamp of the last message attributed to the run; <c>null</c> when the run left no message to date it.</param>
/// <param name="MessageIds">Ids of the run's messages in chronological order.</param>
/// <param name="ToolCalls">Tool calls issued by the run, each linked to its result.</param>
/// <param name="ErrorMessage">User-facing error text when <see cref="StopReason" /> is <see cref="RunStopReason.Failed" />; otherwise null.</param>
public sealed record RunOutcome(
    RunId RunId,
    string SessionId,
    RunStopReason StopReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> MessageIds,
    IReadOnlyList<ToolCallLink> ToolCalls,
    string? ErrorMessage = null)
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

        // #993: null until a message dates it. The previous seed
        // (`startedAt = finishedAt = DateTimeOffset.UtcNow`) was only ever read when
        // the loop body did not run at all, i.e. for an empty history — so it
        // fabricated a zero-length run exactly when there was no run, and left
        // UtcNow as the only timestamp a caller could ever see for one.
        DateTimeOffset? startedAt = null;
        DateTimeOffset? finishedAt = null;

        for (int i = 0; i < messages.Count; i++)
        {
            AgentMessage message = messages[i];
            messageIds.Add(message.Id);

            if (startedAt is null || message.CreatedAt < startedAt.Value)
                startedAt = message.CreatedAt;
            if (finishedAt is null || message.CreatedAt > finishedAt.Value)
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
        else if (lastAssistant is null)
            // #993: no assistant message => nothing was persisted showing the loop
            // exiting cleanly. Ordering matters: a caller-supplied cancellation or
            // error is evidence and is honoured above, so only the *absence* of
            // evidence lands here. Succeeded below is reachable exactly when a
            // terminal assistant message exists to support the claim.
            stopReason = RunStopReason.Stopped;
        else if (lastAssistant.StopReason == global::Harbor.Abstractions.Models.StopReason.Aborted)
            stopReason = RunStopReason.Stopped;
        else if (lastAssistant.StopReason == global::Harbor.Abstractions.Models.StopReason.Error)
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
