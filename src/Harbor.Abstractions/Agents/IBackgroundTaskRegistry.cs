namespace Harbor.Abstractions.Agents;

/// <summary>
///     Terminal outcome of one BACKGROUND sub-agent run, drained from the
///     registry by the orchestrator loop and injected into the session as a
///     follow-up tool result ("boss, I'm done").
/// </summary>
/// <param name="Id">Registry handle returned to the parent at launch (<c>task_N</c>).</param>
/// <param name="AgentName">Name of the sub-agent definition used.</param>
/// <param name="Result">The run summary, or failure (run error, crash, cancellation).</param>
public sealed record BackgroundTaskCompletion(
    string Id,
    string AgentName,
    Result<SubAgentRunResult> Result);

/// <summary>
///     Registry of detached sub-agent runs. The <c>task</c> tool launches runs
///     here when asked with <c>background: true</c> and returns immediately;
///     the orchestrator (<see cref="IAgentLoop" />) drains finished runs per
///     turn and feeds their reports back into the session, so the parent
///     agent keeps working meanwhile and gets pinged on completion.
/// </summary>
/// <remarks>
///     Declared in Domain next to <see cref="ISubAgentRunner" /> for the same
///     reason: <c>Harbor.Tools.Builtin</c>'s <c>TaskTool</c> depends on the
///     abstraction, the implementation lives in the Application layer.
///     All members are thread-safe; runs are keyed to the launching session
///     so drains never leak results across sessions.
/// </remarks>
public interface IBackgroundTaskRegistry
{
    /// <summary>Maximum concurrent background runs (launch fails past the cap).</summary>
    public int MaxBackgroundTasks { get; }

    /// <summary>Currently tracked runs (running + finished-not-drained).</summary>
    public int PendingCount { get; }

    /// <summary>
    ///     Launch a detached run. The <paramref name="run" /> delegate receives a
    ///     cancellation token the caller owns (aborting the parent run cancels
    ///     the background run; turn boundaries do not).
    /// </summary>
    /// <param name="agentName">Sub-agent name (for reports and logs).</param>
    /// <param name="sessionId">Launching session — completions drain only here.</param>
    /// <param name="run">The run itself (usually <see cref="ISubAgentRunner.RunAsync" />).</param>
    /// <param name="ct">Cancellation token owned by the launcher.</param>
    /// <returns>Registry handle (<c>task_N</c>), or failure when over the cap.</returns>
    public Result<string> Start(
        string agentName,
        string sessionId,
        Func<CancellationToken, Task<Result<SubAgentRunResult>>> run,
        CancellationToken ct);

    /// <summary>
    ///     Take all finished runs for <paramref name="sessionId" /> and forget
    ///     them (success, failure, crash and cancellation all surface as
    ///     completions — a stuck run never blocks the registry).
    /// </summary>
    public IReadOnlyList<BackgroundTaskCompletion> DrainCompleted(string sessionId);
}
