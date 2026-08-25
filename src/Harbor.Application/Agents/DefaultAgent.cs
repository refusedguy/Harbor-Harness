using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Agents;
/// <summary>
///     Default IAgent implementation. Stateful wrapper around <see cref="AgentLoop" />.
///     Implements Command pattern (GOF) — encapsulates prompt submission and execution.
/// </summary>
public sealed class DefaultAgent : IAgent
{
    private readonly IAgentLoop _agentLoop;
    private readonly IEventBus _eventBus;
    private readonly IDisposable _eventBusSubscription;
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = new();
    private readonly object _listenersLock = new();
    private readonly ILogger<DefaultAgent> _logger;
    /// <summary>
    ///     Mutual exclusion for <see cref="PromptAsync" /> entry. The previous
    ///     check-then-act <c>State?.IsRunning == true</c> guard was not
    ///     synchronized, so two concurrent calls could both pass it and start
    ///     overlapping runs on the same session. <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)" />
    ///     with a zero timeout performs a single atomic acquire attempt: the
    ///     losing caller fails fast with a "busy" failure instead of racing.
    /// </summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ISessionStore _sessionStore;
    private readonly Channel<AgentMessage> _steeringQueue;

    /// <summary>
    ///     Backing field for <see cref="AbortSource" />. Replaced wholesale by
    ///     <see cref="ResetAbortSource" /> — a single CTS can only be cancelled
    ///     once, so after every abort we swap in a fresh one or the next
    ///     <see cref="PromptAsync" /> would observe the cancelled token and
    ///     fail immediately.
    /// </summary>
    private CancellationTokenSource _abortSource = new();

    /// <summary>
    ///     Per-run completion source. A fresh instance is swapped in at the
    ///     start of every <see cref="PromptAsync" /> call (see
    ///     <see cref="StartRunCompletion" />) and completed with the run's
    ///     <see cref="Result" /> when the run finishes (success, cancel, or
    ///     fault). <see cref="WaitForIdleAsync" /> awaits the snapshot captured
    ///     at call time, so a session switch that aborts the in-flight run will
    ///     see the abort's <c>Result.Failure</c> (set by the cancel branch) and
    ///     return promptly instead of hanging forever.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Marked <c>volatile</c> so the read in <see cref="WaitForIdleAsync" />
    ///         is an acquire load — without <c>volatile</c>, a thread could see
    ///         a stale pre-swap TCS reference and await a task that is never
    ///         completed (the original v0 bug: the readonly field was created
    ///         once in the ctor and <c>SetResult</c> was never called, so
    ///         <c>WaitForIdleAsync</c> hung indefinitely when the agent was
    ///         running).
    ///     </para>
    /// </remarks>
    private volatile TaskCompletionSource<Result> _runCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Construct a <see cref="DefaultAgent" /> wired to the supplied services.
    /// </summary>
    /// <param name="sessionStore">The session store for loading session context.</param>
    /// <param name="agentLoop">The agent loop to drive.</param>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    /// <param name="logger">The logger.</param>
    /// <summary>
    ///     Construct a <see cref="DefaultAgent" /> wired to the supplied services.
    /// </summary>
    /// <param name="sessionStore">The session store for loading session context.</param>
    /// <param name="agentLoop">The agent loop to drive.</param>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    /// <param name="logger">The logger.</param>
    public DefaultAgent(
        ISessionStore sessionStore,
        IAgentLoop agentLoop,
        IEventBus eventBus,
        ILogger<DefaultAgent> logger)
    {
        _sessionStore = sessionStore;
        _agentLoop = agentLoop;
        _eventBus = eventBus;
        _logger = logger;

        _steeringQueue = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _eventBusSubscription = _eventBus.Subscribe(async (evt, ct) =>
        {
            // Snapshot listeners under the lock, then iterate the snapshot outside the lock.
            // Previously this allocated a fresh List<T> via ToList() on every published event,
            // which is significant for high-frequency events like MessageUpdateEvent.
            Func<AgentEvent, CancellationToken, ValueTask>[] snapshot;
            lock (_listenersLock)
            {
                int count = _listeners.Count;
                if (count == 0) return;
                snapshot = new Func<AgentEvent, CancellationToken, ValueTask>[count];
                for (int i = 0; i < count; i++)
                {
                    snapshot[i] = _listeners[i];
                }
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    await snapshot[i](evt, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Listener failed: session={SessionId}", State?.SessionId ?? "unbound");
                }
            }
        });
    }

    /// <summary>
    ///     Current agent state snapshot. <see langword="null" /> until <see cref="Initialize" /> is called.
    /// </summary>
    public AgentState State { get; private set; } = null!;

    /// <summary>
    ///     Cancellation token source used to abort the current run. Replaced
    ///     wholesale by <see cref="ResetAbortSource" /> after each abort so the
    ///     agent is ready for a new run.
    /// </summary>
    public CancellationTokenSource AbortSource => _abortSource;

    /// <summary>
    ///     Recreate <see cref="AbortSource" /> if (and only if) the current
    ///     source has already been cancelled. No-op when the current source is
    ///     still live, so calling this in the middle of a run is safe but does
    ///     nothing. After this returns, <see cref="AbortSource" /> points at a
    ///     fresh, un-cancelled <see cref="CancellationTokenSource" />.
    /// </summary>
    public void ResetAbortSource()
    {
        // Only swap if the current source is already cancelled — otherwise
        // we'd blow away the cancellation token the in-flight PromptAsync is
        // linked to. The CAS below makes the read→swap→dispose sequence
        // atomic (F2): two concurrent resets can no longer double-dispose or
        // publish two sources with a lost window in between.
        CancellationTokenSource observed = Volatile.Read(ref _abortSource);
        if (!observed.IsCancellationRequested)
        {
            return;
        }

        var fresh = new CancellationTokenSource();
        CancellationTokenSource winner = Interlocked.CompareExchange(ref _abortSource, fresh, observed);
        if (ReferenceEquals(winner, observed))
        {
            observed.Dispose();
        }
        else
        {
            fresh.Dispose(); // another caller completed the reset first
        }
    }

    /// <summary>
    ///     Subscribe a listener to all agent events. The listener is invoked for every event
    ///     published to the <see cref="IEventBus" /> by this agent's run.
    /// </summary>
    /// <param name="listener">Async callback.</param>
    /// <returns>A disposable that unsubscribes on dispose.</returns>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_listenersLock)
        {
            _listeners.Add(listener);
        }

        return new Unsubscriber(() =>
        {
            lock (_listenersLock)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>
    ///     Submit a plain-text prompt and run the agent loop to completion.
    ///     <para>
    ///         Ф2/B1: when a run is already active, the text is NOT rejected —
    ///         it is routed into the steering queue (the run picks it up at
    ///         the next safe boundary) and this call returns
    ///         <see cref="Result.Success()" /> immediately. Callers that need
    ///         run-completion semantics must await <see cref="WaitForIdleAsync" />
    ///         after a steered submission.
    ///     </para>
    /// </summary>
    /// <param name="text">The user's prompt text.</param>
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortSource" />.</param>
    /// <returns>Success on completion (or after steering an active run), or failure with an error message.</returns>
    public async Task<Result> PromptAsync(string text, CancellationToken ct = default)
    {
        if (State is null)
            return Result.Failure("Agent is not initialized. Call InitializeAsync first.");

        var userMessage = new UserMessage(
            Guid.NewGuid().ToString("N"),
            State.SessionId,
            DateTimeOffset.UtcNow,
            text,
            State.Agent.Name.Value,
            State.Agent.Model);

        // Ф2/B1: prompt during an active run → steer it instead of failing.
        if (State.IsRunning)
        {
            Steer(userMessage);
            return Result.Success();
        }

        return await PromptAsync(userMessage, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Submit a pre-built <see cref="UserMessage" /> and run the agent loop to completion.
    ///     <para>
    ///         Ф2/B1: when the run gate is held by an active run, the message
    ///         is routed into the steering queue and this call returns
    ///         <see cref="Result.Success()" /> immediately (steer-ack) instead
    ///         of failing with "busy".
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Entry is mutually exclusive via <see cref="_runGate" />: concurrent
    ///     callers are serialized atomically; the loser steers the active run.
    /// </remarks>
    /// <param name="message">The user message to submit.</param>
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortSource" />.</param>
    /// <returns>Success on completion (or after steering an active run), or failure with an error message.</returns>
    public async Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
    {
        if (State is null)
            return Result.Failure("Agent is not initialized.");

        bool acquired;
        try
        {
            // Zero-timeout acquire: a single atomic attempt — never blocks.
            acquired = await _runGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Agent was cancelled.");
        }

        if (!acquired)
        {
            // Ф2/B1: busy → deliver as steering instead of dropping the input.
            // The IsRunning re-check covers the narrow window where the gate is
            // still held but the run already flipped its state to idle — in
            // that case steering would strand the message until an unknown
            // future run, so we keep the explicit failure instead.
            if (State.IsRunning)
            {
                _logger.LogInformation(
                    "Agent {Agent} is running; prompt routed to steering queue",
                    State.Agent.Name.Value);
                Steer(message);
                return Result.Success();
            }

            return Result.Failure("Agent is busy. Wait for the current run to complete or use Steer() to interrupt it.");
        }

        try
        {
            // G1 self-heal: an aborted run leaves AbortSource permanently cancelled,
            // so every later prompt would die on an already-cancelled token unless
            // someone remembered to reset it (IPC abort paths never did). Resetting
            // here, under the run gate, removes that external temporal coupling.
            if (_abortSource.IsCancellationRequested)
            {
                ResetAbortSource();
            }

            // F14: a failed persist used to be invisible — the run continued,
            // the reloaded context lacked the user's message (model answered
            // stale history), and memory/disk/model diverged silently. Fail
            // the run BEFORE any completion-source/state swap instead.
            Result persisted = await _sessionStore.AppendMessageAsync(State.SessionId, message, ct)
                .ConfigureAwait(false);
            if (persisted.IsFailure)
            {
                _logger.LogError(
                    "Failed to persist user message {MessageId} for session {SessionId}: {Error}",
                    message.Id, State.SessionId, persisted.Error);
                return Result.Failure($"Failed to persist prompt: {persisted.Error}");
            }

            // Swap in a fresh per-run completion source so WaitForIdleAsync callers
            // awaiting the PREVIOUS source see THIS run's terminal result. The old
            // source is completed with a cancelled sentinel to unblock any caller
            // that raced between State.IsRunning flipping true and the swap.
            var completion = StartRunCompletion();

            State = State with { IsRunning = true, StartedAt = DateTimeOffset.UtcNow };

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(AbortSource.Token, ct);

            try
            {
                var session = await LoadSessionContextAsync(State.SessionId, ct).ConfigureAwait(false);
                var result = await _agentLoop.RunAsync(session, State.Agent, linkedCts.Token).ConfigureAwait(false);

                State = State with
                {
                    IsRunning = false,
                    LastActivityAt = DateTimeOffset.UtcNow
                };

                completion.TrySetResult(result);
                return result;
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                State = State with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow };
                var cancelled = Result.Failure("Agent was cancelled.");
                completion.TrySetResult(cancelled);
                return cancelled;
            }
            catch (Exception ex)
            {
                State = State with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow };
                _logger.LogError(ex, "Agent run failed: session={SessionId}", State.SessionId);
                var failed = Result.Failure(ex.Message);
                completion.TrySetException(ex);
                return failed;
            }
            finally
            {
                linkedCts.Dispose();
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    ///     Inject a steering message into the current run. The message is processed at the next
    ///     safe boundary (mid-turn after tool results, or between turns — Ф2/B2).
    /// </summary>
    /// <param name="message">The message to inject.</param>
    public void Steer(AgentMessage message) => _steeringQueue.Writer.TryWrite(message);

    /// <summary>
    ///     Wait for the agent to become idle (no <see cref="PromptAsync" /> in flight).
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task that completes when the agent is idle.</returns>
    /// <remarks>
    ///     <para>
    ///         Captures <see cref="_runCompletion" /> into a local before
    ///         returning the awaiting task. Without the local, the lambda
    ///         closure would re-read the volatile field at await time and could
    ///         pick up the NEXT run's source (if a new PromptAsync started
    ///         between the State.IsRunning check and the await) — that would
    ///         make this method return the wrong run's terminal result.
    ///     </para>
    /// </remarks>
    public Task WaitForIdleAsync(CancellationToken ct = default)
    {
        if (State?.IsRunning != true)
        {
            return Task.CompletedTask;
        }

        // Capture the current completion source — THIS is the run we want to
        // wait on, even if a new PromptAsync swaps in a fresh one while we're
        // parked on the await.
        var completion = _runCompletion;
        return Task.Run(async () => await completion.Task.ConfigureAwait(false), ct);
    }

    /// <summary>
    ///     Bind this agent to a session + agent definition. Must be called before the first
    ///     <see cref="PromptAsync" /> call.
    /// </summary>
    /// <param name="session">The session to bind to.</param>
    /// <param name="agent">The agent definition to use.</param>
    public void Initialize(Session session, AgentDefinition agent)
    {
        // G2: the steering channel is created once per agent lifetime and
        // survives rebinds. Stale messages authored for a PREVIOUS session
        // would otherwise be drained into the new session's history on its
        // first run. Same-session rebind keeps queued steering intact.
        if (State is not null && !string.Equals(State.SessionId, session.Id, StringComparison.Ordinal))
        {
            int dropped = 0;
            while (_steeringQueue.Reader.TryRead(out _))
            {
                dropped++;
            }

            if (dropped > 0)
            {
                _logger.LogWarning(
                    "Dropped {Count} stale steering message(s) while rebinding agent from session {OldSession} to {NewSession}",
                    dropped, State.SessionId, session.Id);
            }
        }

        State = AgentState.Idle(session.Id, agent);
    }

    /// <summary>
    ///     Release all resources held by this agent: event-bus subscription, abort source,
    ///     run gate, steering channel.
    /// </summary>
    public void Dispose()
    {
        _eventBusSubscription?.Dispose();
        AbortSource?.Dispose();
        _runGate.Dispose();
        _steeringQueue.Writer.TryComplete();
    }

    /// <summary>
    ///     Atomically swap <see cref="_runCompletion" /> for a fresh
    ///     <see cref="TaskCompletionSource{TResult}" /> and complete the previous
    ///     one (if any) with a <c>Result.Failure("Agent was cancelled.")</c>
    ///     sentinel so any <see cref="WaitForIdleAsync" /> caller awaiting it
    ///     returns promptly.
    /// </summary>
    /// <returns>The new completion source for this run.</returns>
    private TaskCompletionSource<Result> StartRunCompletion()
    {
        var fresh = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _runCompletion, fresh);
        previous?.TrySetResult(Result.Failure("Agent was cancelled."));
        return fresh;
    }

    private async Task<ISessionContext> LoadSessionContextAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure)
            throw new InvalidOperationException(session.Error);

        var messages = await _sessionStore.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messages.IsFailure)
            throw new InvalidOperationException(messages.Error);

        return new DefaultSessionContext(session.Value, messages.Value, _sessionStore, _steeringQueue);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _action;
        public Unsubscriber(Action action)
        {
            _action = action;
        }
        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}

/// <summary>
///     Default session context implementation.
/// </summary>
internal sealed class DefaultSessionContext : ISessionContext
{
    private readonly List<AgentMessage> _messages;
    private readonly ISessionStore _store;
    private readonly ILogger _logger;

    public DefaultSessionContext(
        Session session,
        IReadOnlyList<AgentMessage> messages,
        ISessionStore store,
        Channel<AgentMessage> steeringQueue,
        ILogger? logger = null)
    {
        Session = session;
        // Pre-size the internal list to the known count. The previous `.ToList()` always
        // allocated a new List<T> with a default capacity then grew it via doubling.
        _messages = new List<AgentMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            _messages.Add(messages[i]);
        }
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        SteeringQueue = steeringQueue;
    }

    public Session Session { get; }

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public Channel<AgentMessage> SteeringQueue
    {
        get;
    }

    public async Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        // Memory-first by design (F14 detail): the store append below must not
        // lose its Result silently — a failed write means disk diverges from
        // memory/model, so surface it in the log keyed by session.
        _messages.Add(message);
        Result persisted = await _store.AppendMessageAsync(Session.Id, message, ct).ConfigureAwait(false);
        if (persisted.IsFailure)
        {
            _logger.LogError(
                "Failed to persist message {MessageId} for session {SessionId}: {Error}",
                message.Id, Session.Id, persisted.Error);
        }
    }

    public async Task UpdateStatsAsync(Usage usage, CancellationToken ct = default)
    {
        var stats = await _store.GetStatsAsync(Session.Id, ct).ConfigureAwait(false);
        if (stats.IsFailure)
        {
            _ = stats.Error;
            return;
        }

        var updated = stats.Value.AddUsage(usage);
        await _store.UpdateStatsAsync(Session.Id, updated, ct).ConfigureAwait(false);
    }
}
