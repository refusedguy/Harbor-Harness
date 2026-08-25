namespace Harbor.Application.Agents;

/// <summary>
///     Dispatches tool calls to registered <see cref="ITool" />s and aggregates
///     results into a <see cref="ToolResultMessage" /> (ROP-C П.5). Extracted
///     behind an interface so <see cref="AgentLoop" /> depends on the seam, not
///     the concrete dispatcher — decorators (telemetry, permissions audit) and
///     test doubles plug in via DI.
/// </summary>
public interface IToolDispatcher
{
    /// <summary>
    ///     Execute a batch of tool calls either sequentially (if any tool
    ///     declares <see cref="ExecutionMode.Sequential" />) or in parallel.
    /// </summary>
    /// <param name="toolCalls">The tool calls emitted by the model this turn.</param>
    /// <param name="session">The session context (used for tool contexts).</param>
    /// <param name="partial">The assistant message being streamed.</param>
    /// <param name="agent">The agent definition driving the run (permission source).</param>
    /// <param name="ct">Cancellation token for the whole batch.</param>
    /// <param name="toolExecutionTimeout">
    ///     Optional per-call deadline (A9); when it fires, an error entry is
    ///     synthesized so the loop keeps going.
    /// </param>
    /// <returns>A tool-result message ready to append to the session history.</returns>
    Task<ToolResultMessage> ExecuteAsync(
        IReadOnlyList<ToolCallPart> toolCalls,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout = null);
}
