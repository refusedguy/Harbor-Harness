using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Agents;

/// <summary>
///     One sub-agent delegation request, authored by the parent agent's
///     <c>task</c> tool call.
/// </summary>
/// <param name="Prompt">Self-contained task description for the sub-agent.</param>
/// <param name="ParentSessionId">
///     The session the <c>task</c> call came from. Recorded on the spawned
///     session as <c>ParentSessionId</c> so sub-runs are traceable in the
///     session list without leaking into the parent history.
/// </param>
/// <param name="WorkingDirectory">
///     Working directory bound to the spawned session. Falls back to the
///     process working directory when omitted.
/// </param>
/// <param name="ParentMessageId">
///     Id of the parent assistant message that issued the <c>task</c> call.
///     The runner folds the child's token usage into that message (and the
///     parent session stats) so costs stay visible on the parent side.
///     Null skips the message update — stats-only propagation.
/// </param>
public sealed record SubAgentRunRequest(
    string Prompt,
    string? ParentSessionId = null,
    string? WorkingDirectory = null,
    string? ParentMessageId = null);

/// <summary>
///     Terminal outcome of one sub-agent run: where it happened and what the
///     final assistant answer was.
/// </summary>
/// <param name="SessionId">Id of the isolated session the run executed in.</param>
/// <param name="AgentName">Name of the sub-agent definition used.</param>
/// <param name="FinalOutput">
///     Concatenated text of the last assistant message produced by the run —
///     this is exactly what surfaces back to the parent as tool output.
/// </param>
/// <param name="NewMessages">
///     Total message count persisted in the sub-session (user prompt, assistant
///     messages, tool traffic). Purely informational for the parent summary.
/// </param>
/// <param name="ChildUsage">
///     Aggregated token usage of the child run (sum over its assistant
///     messages). The runner folds this delta into the parent message/stats;
///     it is also surfaced here so the <c>task</c> tool can render the cost.
///     Null (legacy call sites) means zero.
/// </param>
public sealed record SubAgentRunResult(
    string SessionId,
    string AgentName,
    string FinalOutput,
    int NewMessages,
    Usage? ChildUsage = null)
{
    /// <summary>
    ///     Whether the child consumed any billable tokens. Cost suffixes and
    ///     propagation are skipped when this is <see langword="false" />.
    /// </summary>
    public bool HasUsage => ChildUsage is not null
        && (ChildUsage.InputTokens > 0
            || ChildUsage.OutputTokens > 0
            || (ChildUsage.ReasoningTokens ?? 0) > 0
            || (ChildUsage.CacheReadTokens ?? 0) > 0
            || (ChildUsage.CacheWriteTokens ?? 0) > 0);
}

/// <summary>
///     Machine-readable resume pointer for a failed sub-agent run: the
///     sub-session id whose partial history survived the failure, so the
///     parent can continue the same sub-chat instead of starting over.
/// </summary>
/// <param name="SessionId">Id of the failed run's isolated session.</param>
/// <param name="AgentName">Name of the sub-agent definition that failed.</param>
public sealed record SubAgentResumeHint(string SessionId, string AgentName);

/// <summary>
///     Stable text format for the resumable id carried by sub-agent failures
///     ([UX6] #266). <see cref="Result{T}.Error" /> is string-only, so the
///     session id travels as a machine-readable trailer inside the message:
///     prose for the parent model, the bracket for programmatic extraction via
///     <see cref="TryExtractResumeHint" /> (attached to the tool result as
///     <see cref="SubAgentResumeHint" /> metadata).
/// </summary>
public static class SubAgentFailureFormat
{
    /// <summary>
    ///     Marker opening the resume trailer. The session id follows it up to
    ///     the closing <c>]</c>. Stable contract — do not rephrase.
    /// </summary>
    public const string ResumeMarker = "[resume-session:";

    /// <summary>
    ///     Append the resume trailer for <paramref name="sessionId" /> to a
    ///     failure message.
    /// </summary>
    public static string WithResumeTrailer(string message, string sessionId) =>
        $"{message} {ResumeMarker}{sessionId}]";

    /// <summary>
    ///     Extract the resume hint from a failure message produced by
    ///     <see cref="WithResumeTrailer" />, or <see langword="null" /> when
    ///     the message carries no resumable session (e.g. validation or
    ///     nesting-guard failures that never spawned one).
    /// </summary>
    public static SubAgentResumeHint? TryExtractResumeHint(string error, string agentName)
    {
        if (string.IsNullOrEmpty(error))
            return null;
        int marker = error.LastIndexOf(ResumeMarker, StringComparison.Ordinal);
        if (marker < 0)
            return null;
        int idStart = marker + ResumeMarker.Length;
        int idEnd = error.IndexOf(']', idStart);
        if (idEnd <= idStart)
            return null;
        string sessionId = error.Substring(idStart, idEnd - idStart);
        if (sessionId.Length == 0)
            return null;
        for (int i = 0; i < sessionId.Length; i++)
        {
            char c = sessionId[i];
            if (char.IsWhiteSpace(c) || c is '[' or ']')
                return null;
        }

        return new SubAgentResumeHint(sessionId, agentName);
    }
}

/// <summary>
///     Runs a sub-agent end-to-end in an ISOLATED session: spawns a fresh
///     <see cref="Harbor.Abstractions.Models.Session" />, drives the agent loop on the given
///     <see cref="AgentDefinition" /> with the request's prompt, and returns the final
///     assistant output as the tool result for the parent agent.
/// </summary>
/// <remarks>
///     <para>
///         Declared in Domain so <c>Harbor.Tools.Builtin</c>'s <c>TaskTool</c> can depend on
///         the abstraction while the implementation (<c>SubAgentRunner</c>) lives in the
///         Application layer next to <see cref="IAgentLoop" />.
///     </para>
///     <para>
///         Implementations MUST enforce a nesting guard: a running sub-agent MUST NOT be
///         able to invoke <c>task</c> again (<see cref="CanSpawn" /> reports
///         <see langword="false" /> while a run is in flight on the logical call chain).
///     </para>
/// </remarks>
public interface ISubAgentRunner
{
    /// <summary>
        ///     Whether a NEW sub-agent spawn is legal from the current async call chain.
        ///     <see langword="true" /> at top level; <see langword="false" /> while this chain is
        ///     already executing inside a sub-agent run (recursion guard).
        /// </summary>
    public bool CanSpawn { get; }

    /// <summary>
    ///     Execute the sub-agent described by <paramref name="agent" /> on
    ///     <paramref name="request" />'s prompt in an isolated session and wait for
    ///     completion.
    /// </summary>
    /// <param name="agent">The sub-agent definition (must have IsSubAgent=true).</param>
    /// <param name="request">Prompt plus optional parent linkage metadata.</param>
    /// <param name="ct">Cancellation token propagated into the whole sub-run.</param>
    /// <returns>The run summary, or failure with an error message.</returns>
    public Task<Result<SubAgentRunResult>> RunAsync(AgentDefinition agent, SubAgentRunRequest request, CancellationToken ct = default);
}
