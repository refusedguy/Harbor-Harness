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
public sealed record SubAgentRunRequest(
    string Prompt,
    string? ParentSessionId = null,
    string? WorkingDirectory = null);

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
public sealed record SubAgentRunResult(
    string SessionId,
    string AgentName,
    string FinalOutput,
    int NewMessages);

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
