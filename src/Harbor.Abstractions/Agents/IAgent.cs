using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
namespace Harbor.Abstractions.Agents;
/// <summary>
///     Minimal runner surface for an agent — the subset of <see cref="IAgent" /> that
///     presentation and orchestration layers need to drive a conversation.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists (§ARCH-002):</b> before this interface was extracted,
///         <c>Harbor.Terminal.Abstractions</c> depended on the full <see cref="IAgent" />
///         contract (which includes <see cref="IAgent.Subscribe" />,
///         <see cref="IAgent.Steer" />, <see cref="IAgent.FollowUp" />,
///         <see cref="IAgent.Initialize" /> — none of which a TUI effect host calls).
///         That violated the Interface Segregation Principle and made the TUI layer
///         rebuild whenever any IAgent member changed.
///     </para>
///     <para>
///         <see cref="IAgent" /> extends this interface, so any code that already takes
///         <c>IAgent</c> keeps working. New code that only needs to "send a prompt and
///         wait for idle" should take <see cref="IAgentRunner" />.
///     </para>
///     <para>
///         <b>Layering:</b> declared in <c>Harbor.Abstractions</c> (Domain) so that
///         <c>Harbor.Terminal.Abstractions</c> (also Domain) can reference it without going
///         through <c>Harbor.Core</c> (Application).
///     </para>
/// </remarks>
public interface IAgentRunner
{
    /// <summary>
    ///     Cancellation token source used to abort the current run. Call
    ///     <see cref="CancellationTokenSource.Cancel" /> to interrupt the agent at the
    ///     next safe boundary (between turns or during a streaming await).
    /// </summary>
    public CancellationTokenSource AbortSource { get; }

    /// <summary>
    ///     Submit a user prompt as plain text and run the agent loop to completion.
    /// </summary>
    /// <param name="text">The user's prompt text.</param>
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortSource" />.</param>
    /// <returns>Success on completion, or failure with an error message.</returns>
    public Task<Result> PromptAsync(string text, CancellationToken ct = default);

    /// <summary>
    ///     Wait for the agent to become idle (no <see cref="PromptAsync" /> call in flight).
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task that completes when the agent is idle.</returns>
    public Task WaitForIdleAsync(CancellationToken ct = default);

    /// <summary>
    ///     Recreate the internal <see cref="AbortSource" /> so the agent is ready
    ///     for a fresh run after the previous token was cancelled. No-op if the
    ///     current source has not been cancelled (calling this in the middle of a
    ///     run would blow away the in-flight cancellation token, so the guard
    ///     prevents footguns).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists:</b> a single <c>CancellationTokenSource</c> can
    ///         only transition to the cancelled state once. After an abort (user
    ///         Stop button, session switch, etc.) the source stays cancelled and
    ///         every subsequent <see cref="PromptAsync" /> call would immediately
    ///         observe <c>IsCancellationRequested=true</c> and fail with
    ///         <c>OperationCanceledException</c> — the user could never send
    ///         another prompt. The session manager also uses this to reset the
    ///         abort state before binding a new session so the new session's
    ///         first prompt isn't dead on arrival.
    ///     </para>
    /// </remarks>
    public void ResetAbortSource();
}

/// <summary>
///     Stateful agent wrapper around the agent loop pipeline.
/// </summary>
/// <remarks>
///     <para>
///         An <see cref="IAgent" /> owns the runtime state for a single conversation: the
///         active <see cref="AgentState" />, the steering/follow-up queues, the abort token,
///         and the listener fan-out for agent events. The agent is paired with a
///         <see cref="Session" /> + <see cref="AgentDefinition" /> via
///         <see cref="Initialize" /> before its first <see cref="PromptAsync" /> call.
///     </para>
///     <para>
///         Implementations MUST be thread-safe for <see cref="Subscribe" />, <see cref="Steer" />,
///         and <see cref="FollowUp" />. <see cref="PromptAsync" /> is single-flight: a second call
///         while the agent is running returns <see cref="Result.Failure" />.
///     </para>
///     <para>
///         The default implementation is <c>DefaultAgent</c> in <c>Harbor.Core</c>.
///     </para>
///     <para>
///         <b>ISP note (§ARCH-002):</b> the runner surface (<see cref="AbortSource" />,
///         <see cref="PromptAsync(string, CancellationToken)" />,
///         <see cref="WaitForIdleAsync" />) is also exposed on
///         <see cref="IAgentRunner" />. Callers that do not need steering / follow-up /
///         subscription should take <see cref="IAgentRunner" /> instead.
///     </para>
/// </remarks>
public interface IAgent : IAgentRunner, IDisposable
{
    /// <summary>
    ///     Current agent state snapshot. <see cref="AgentState.IsRunning" /> reflects whether
    ///     a <see cref="PromptAsync" /> call is in flight.
    /// </summary>
    public AgentState State { get; }

    /// <summary>
    ///     Subscribe to all <see cref="AgentEvent" />s emitted by this agent. The returned
    ///     <see cref="IDisposable" /> unsubscribes the listener when disposed.
    /// </summary>
    /// <param name="listener">Async callback invoked for every event.</param>
    /// <returns>An <see cref="IDisposable" /> that removes the listener on dispose.</returns>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener);

    /// <summary>
    ///     Submit a pre-built <see cref="UserMessage" /> and run the agent loop to completion.
    /// </summary>
    /// <param name="message">The fully-formed user message (id, timestamp, etc. supplied by caller).</param>
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortSource" />.</param>
    /// <returns>Success on completion, or failure with an error message.</returns>
    public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default);

    /// <summary>
    ///     Initialize the agent with a session and definition.
    /// </summary>
    /// <param name="session">The conversation session to bind to.</param>
    /// <param name="agent">The agent definition that drives the loop.</param>
    public void Initialize(Session session, AgentDefinition agent);

    /// <summary>
    ///     Interrupt current turn at next safe boundary.
    /// </summary>
    /// <param name="message">A message to inject into the steering queue.</param>
    public void Steer(AgentMessage message);

    /// <summary>
    ///     Queue a follow-up message after current turn completes.
    /// </summary>
    /// <param name="message">A message to append after the current run finishes.</param>
    public void FollowUp(AgentMessage message);
}

/// <summary>
///     Agent state snapshot.
/// </summary>
/// <param name="SessionId">Id of the bound session.</param>
/// <param name="Agent">The agent definition bound to the session.</param>
/// <param name="IsRunning">Whether a <see cref="IAgent.PromptAsync" /> call is currently in flight.</param>
/// <param name="CurrentTurn">The turn index of the in-flight run, or 0 when idle.</param>
/// <param name="StartedAt">When the current run started, or <see langword="null" /> when idle.</param>
/// <param name="LastActivityAt">When the agent last emitted an event.</param>
public sealed record AgentState(
    string SessionId,
    AgentDefinition Agent,
    bool IsRunning,
    int CurrentTurn,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt)
{
    /// <summary>
    ///     Returns an idle <see cref="AgentState" /> for a freshly bound session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="agent">The agent definition bound to the session.</param>
    /// <returns>An <see cref="AgentState" /> with <see cref="IsRunning" /> = <see langword="false" />.</returns>
    public static AgentState Idle(string sessionId, AgentDefinition agent) => new(
        sessionId,
        agent,
        false,
        0,
        null,
        null);
}

/// <summary>
///     Agent loop — the core pipeline of LLM call → tool execution → next turn.
///     Implements Chain of Responsibility pattern (GOF).
/// </summary>
/// <remarks>
///     <para>
///         The agent loop is the single entry point for advancing a conversation one full turn:
///         build the system prompt, call the LLM, stream tokens to the event bus, execute any tool
///         calls, append the results, and repeat until either there are no more tool calls or the
///         <see cref="AgentDefinition.MaxSteps" /> budget is exhausted.
///     </para>
///     <para>
///         Implementations MUST be stateless across runs — all per-conversation state lives in the
///         <see cref="ISessionContext" /> passed to <see cref="RunAsync" />. The default
///         implementation is <c>AgentLoop</c> in <c>Harbor.Core</c>.
///     </para>
/// </remarks>
public interface IAgentLoop
{
    /// <summary>
    ///     Run the agent loop to completion (no tool calls, max steps reached, or cancelled).
    /// </summary>
    /// <param name="session">The session context providing messages and stats storage.</param>
    /// <param name="agent">The agent definition driving the loop.</param>
    /// <param name="ct">Cancellation token used to abort the run at the next safe boundary.</param>
    /// <returns>Success on normal completion, or failure with an error message.</returns>
    public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default);
}
