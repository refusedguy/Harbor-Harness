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
    private readonly ListenerRegistry _listeners = new();
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
    private readonly AgentSteeringQueue _steering = new();

    /// <summary>
    ///     Backing field for <see cref="AbortToken" />. Replaced wholesale by
    ///     <see cref="ResetAbortSource" /> — a single CTS can only be cancelled
    ///     once, so after every abort we swap in a fresh one or the next
    ///     <see cref="PromptAsync" /> would observe the cancelled token and
    ///     fail immediately. Never exposed: external code observes
    ///     <see cref="AbortToken" /> and aborts via <see cref="RequestAbort" />
    ///     (#79), so the coordinated funnel cannot be bypassed or disposed.
    /// </summary>
    private CancellationTokenSource _abortSource = new();

    /// <summary>
    ///     Abort-source generation (#91). Bumped on every successful
    ///     <see cref="ResetAbortSource" /> swap, starting at 0 for the
    ///     construction-time source. Lets a reset tell whether the in-flight
    ///     run (if any) is bound to the current source or an older one —
    ///     mirroring the cancel generation the approval coordinator already
    ///     tracks, but for the source-swap side.
    /// </summary>
    private long _abortGeneration;

    /// <summary>
    ///     Abort generation the in-flight <see cref="PromptAsync" /> run is
    ///     bound to, or -1 when idle. Captured under the run gate at run
    ///     start, cleared before the gate is released at run end, so
    ///     <see cref="ResetAbortSource" /> can refuse a swap that would orphan
    ///     a live run (a later cancel would hit the fresh source while the old
    ///     run still waits on the disposed one).
    /// </summary>
    private long _activeRunAbortGeneration = -1;

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

        // [G4]: listener fan-out lives in ListenerRegistry; snapshot + invoked
        // outside the lock exactly as before, failures logged per listener.
        _eventBusSubscription = _eventBus.Subscribe(async (evt, ct) =>
        {
            // #559: absence is Maybe, so the "which session is this event for"
            // question travels as Maybe<string> instead of a null the listener
            // registry has to interpret. An unbound agent dispatches with
            // Maybe.None, which the registry logs as "unbound".
            await _listeners.DispatchAsync(evt, ct, _logger, State.Map(static s => s.SessionId)).ConfigureAwait(false);
        });
    }

    /// <summary>
    ///     Backing field for <see cref="State" />. <see langword="null" /> until
    ///     <see cref="Initialize" /> binds a session.
    /// </summary>
    /// <remarks>
    ///     Deliberately a plain nullable reference rather than a
    ///     <c>Maybe&lt;AgentState&gt;</c> field: a reference read/write is a
    ///     single atomic word, which is exactly the guarantee the auto-property
    ///     this replaced gave the event-bus subscription lambda on the frame
    ///     thread. A struct-typed field would be read as several instructions.
    /// </remarks>
    private AgentState? _state;

    /// <summary>
    ///     Current agent state snapshot; <c>Maybe.None</c> until
    ///     <see cref="Initialize" /> is called.
    /// </summary>
    /// <remarks>
    ///     This property used to be declared non-nullable and initialised with
    ///     <c>null!</c> (#559), which put the compiler in the position of
    ///     accepting a value nobody had produced yet — while twenty external
    ///     call sites, three of them in constructors, dereferenced the
    ///     annotation. See <see cref="IAgent.State" /> for why absence is the
    ///     fix and a fabricated sentinel is not.
    /// </remarks>
    public Maybe<AgentState> State => _state is { } bound ? Maybe.From(bound) : Maybe<AgentState>.None;

    /// <summary>
    ///     The reason a read of the bound state answers with when no session has
    ///     been bound yet.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One wording for the whole corridor: the two <see cref="PromptAsync" />
    ///         overloads and the internal read seam name the same condition and the
    ///         same fix, so a caller grepping for the reason finds exactly one
    ///         answer instead of two near-synonyms (#1583).
    ///     </para>
    ///     <para>
    ///         The text names <see cref="Initialize" /> — the method that actually
    ///         binds a session — where the old wording said
    ///         <c>Call InitializeAsync first</c>, a method no caller can invoke:
    ///         <see cref="IAgent.Initialize" /> is the only entry point, and it is
    ///         not async.
    ///     </para>
    /// </remarks>
    private const string NotInitializedDiagnostic =
        "Agent is not initialized. Bind a session with Initialize(session, agent) first.";

    /// <summary>
    ///     <see cref="State" /> as a plain record, for the paths that have
    ///     already established the agent is bound — and a named failure for the
    ///     path that has not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #1583: this used to be a THROWING accessor
    ///         (<c>_state ?? throw new InvalidOperationException(...)</c>). The
    ///         throw sat in the middle of the agent-run corridor: a host or a
    ///         plugin that read the bound state before the bind got an exception
    ///         unwound through the layers instead of a <c>Result.Failure</c>
    ///         naming the fix. "Not bound yet" is an expected answer, not a
    ///         programming error, so the read is Result-shaped end to end now and
    ///         the run boundary hands the reason to the caller.
    ///     </para>
    /// </remarks>
    private Result<AgentState> ReadBoundState()
        => _state is { } bound
            ? Result.Success<AgentState>(bound)
            : Result.Failure<AgentState>(NotInitializedDiagnostic);

    /// <summary>
    ///     Advance the bound state: read the CURRENT snapshot, apply
    ///     <paramref name="project" />, publish the result.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #1583: the write-side counterpart of the Result-shaped
    ///         <see cref="ReadBoundState" /> read, and the reason the throwing
    ///         accessor could go. The old spelling
    ///         (<c>BoundState = BoundState with { ... }</c>) READ the field and
    ///         threw out of the corridor when the field was absent.
    ///     </para>
    ///     <para>
    ///         "Advance the bound state" is only meaningful while a state is
    ///         bound; with none bound there is nothing to advance, and the run
    ///         boundary has already refused the run by then. The read and the
    ///         write stay one step, exactly as they were.
    ///     </para>
    /// </remarks>
    private void AdvanceBoundState(Func<AgentState, AgentState> project)
    {
        if (_state is { } current)
        {
            _state = project(current);
        }
    }

    /// <summary>
    ///     Token observing the current run's abort state. The live source
    ///     stays private (#79) — abort via <see cref="RequestAbort" />.
    ///     Replaced wholesale by <see cref="ResetAbortSource" /> after each
    ///     abort so the agent is ready for a new run.
    /// </summary>
    public CancellationToken AbortToken => Volatile.Read(ref _abortSource).Token;

    /// <summary>
    ///     Abort the current run at the next safe boundary. The only external
    ///     abort ingress on this runner (#79): the approval coordinator calls
    ///     this after sweeping pending gates, and direct callers use it when
    ///     no coordinator is available.
    /// </summary>
    public void RequestAbort() => Volatile.Read(ref _abortSource).Cancel();

    /// <summary>
    ///     Recreate the internal abort source if (and only if) the current
    ///     source has already been cancelled AND no run bound to this (or an
    ///     older) abort generation is still alive. No-op when the current
    ///     source is still live, or when an in-flight run holds it — swapping
    ///     underneath a live run would orphan it from future cancels, so the
    ///     reset is refused and retried later (the next
    ///     <see cref="PromptAsync" /> self-heal runs once idle). After a
    ///     successful reset, <see cref="AbortToken" /> observes a fresh,
    ///     un-cancelled source of the next generation.
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

        // #91: refuse the swap while a run bound to this (or an older)
        // generation is still alive — e.g. a session switch whose bounded
        // WaitForIdleAsync timed out must not swap the token out from under
        // the zombie run; the next prompt would then race it.
        long activeGeneration = Volatile.Read(ref _activeRunAbortGeneration);
        if (activeGeneration >= 0 && activeGeneration <= Volatile.Read(ref _abortGeneration))
        {
            return;
        }

        var fresh = new CancellationTokenSource();
        CancellationTokenSource winner = Interlocked.CompareExchange(ref _abortSource, fresh, observed);
        if (ReferenceEquals(winner, observed))
        {
            Interlocked.Increment(ref _abortGeneration);
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
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) =>
        _listeners.Subscribe(listener);

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
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortToken" />.</param>
    /// <returns>Success on completion (or after steering an active run), or failure with an error message.</returns>
    public async Task<Result> PromptAsync(string text, CancellationToken ct = default)
    {
        // #559: the guard the old `State is null` check wanted, spelled against
        // the field instead of the projection. One read, so a concurrent rebind
        // cannot split the snapshot the message is stamped from.
        // #1583: that one read is now the Result-shaped seam, so a read before
        // Initialize answers with the named reason instead of an exception
        // unwound through the layers.
        Result<AgentState> bound = ReadBoundState();
        if (bound.IsFailure)
            return bound.ConvertFailure();

        AgentState state = bound.Value;

        var userMessage = new UserMessage(
            Guid.NewGuid().ToString("N"),
            state.SessionId,
            DateTimeOffset.UtcNow,
            text,
            state.Agent.Name.Value,
            state.Agent.Model);

        // Ф2/B1: prompt during an active run → steer it instead of failing.
        if (state.IsRunning)
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
    /// <param name="ct">Optional cancellation token linked to <see cref="AbortToken" />.</param>
    /// <returns>Success on completion (or after steering an active run), or failure with an error message.</returns>
    public async Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
    {
        // #1583: the read-before-Initialize seam is Result-shaped, so the run
        // boundary answers with the named reason instead of an exception thrown
        // out of the corridor.
        Result<AgentState> bound = ReadBoundState();
        if (bound.IsFailure)
            return bound.ConvertFailure();

        AgentState state = bound.Value;

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
            // #559: deliberately a FRESH read, not the snapshot taken at the top
            // of the method — seeing a run that started after that snapshot is
            // the whole point of this re-check.
            // #1583: the field, spelled the way WaitForIdleAsync spells it,
            // because "not bound" and "not running" are the same answer here:
            // the seam's failure branch would say exactly this, and the seam
            // must not throw on the way to saying it.
            if (_state is { IsRunning: true })
            {
                DefaultAgentLog.PromptRoutedToSteering(_logger, state.Agent.Name.Value);
                Steer(message);
                return Result.Success();
            }

            return Result.Failure("Agent is busy. Wait for the current run to complete or use Steer() to interrupt it.");
        }

        try
        {
            // G1 self-heal: an aborted run leaves the abort source permanently cancelled,
            // so every later prompt would die on an already-cancelled token unless
            // someone remembered to reset it (IPC abort paths never did). Resetting
            // here, under the run gate, removes that external temporal coupling.
            // #91: the reset is refused while a run is alive, but under this gate
            // the previous run has finished (it clears _activeRunAbortGeneration
            // before releasing the gate), so the self-heal always goes through.
            if (_abortSource.IsCancellationRequested)
            {
                ResetAbortSource();
            }

            // #91: bind this run to the current abort generation so a concurrent
            // ResetAbortSource (e.g. after a WaitForIdleAsync timeout) refuses
            // to swap the source out from under us.
            Volatile.Write(ref _activeRunAbortGeneration, Volatile.Read(ref _abortGeneration));

            // F14: a failed persist used to be invisible — the run continued,
            // the reloaded context lacked the user's message (model answered
            // stale history), and memory/disk/model diverged silently. Fail
            // the run BEFORE any completion-source/state swap instead.
            Result persisted = await _sessionStore.AppendMessageAsync(state.SessionId, message, ct)
                .ConfigureAwait(false);
            if (persisted.IsFailure) // §4.6-ok: F14-политика — специфичный лог + текст провала, одиночный шаг.
            {
                DefaultAgentLog.PersistUserMessageFailed(_logger, message.Id, state.SessionId, persisted.Error);
                return persisted.MapError(static e => $"Failed to persist prompt: {e}");
            }

            // Swap in a fresh per-run completion source so WaitForIdleAsync callers
            // awaiting the PREVIOUS source see THIS run's terminal result. The old
            // source is completed with a cancelled sentinel to unblock any caller
            // that raced between State.IsRunning flipping true and the swap.
            var completion = StartRunCompletion();

            AdvanceBoundState(static s => s with { IsRunning = true, StartedAt = DateTimeOffset.UtcNow });

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(AbortToken, ct);

            try
            {
                var loaded = await LoadSessionContextAsync(state.SessionId, linkedCts.Token).ConfigureAwait(false);
                if (loaded.IsFailure)
                {
                    // #1510: the load failure is a Result this method already holds.
                    // Return it instead of throwing it and catching it back. The old
                    // bridge manufactured an exception out of the failure, and the
                    // catch below turned that exception into a Failure again: a
                    // round trip with no gain. The typed failure died on the way out,
                    // the catch-all picked up exceptions nobody meant it to catch, and
                    // the reader had to reconstruct the cycle from two places in the
                    // file. Nothing outside could depend on the exception type — it
                    // was created and caught inside the same try. The catch below
                    // stays: the agent loop can throw, and a real exception is still
                    // a fault worth logging as one.
                    AdvanceBoundState(static s => s with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow });
                    DefaultAgentLog.LoadSessionContextFailed(_logger, state.SessionId, loaded.Error);
                    // ConvertFailure carries the failure's own text across. It throws
                    // on a success, which is why the guard above is load-bearing.
                    var loadFailed = loaded.ConvertFailure();
                    completion.TrySetResult(loadFailed);
                    return loadFailed;
                }

                var result = await _agentLoop.RunAsync(loaded.Value, state.Agent, linkedCts.Token).ConfigureAwait(false);

                AdvanceBoundState(static s => s with
                {
                    IsRunning = false,
                    LastActivityAt = DateTimeOffset.UtcNow
                });

                completion.TrySetResult(result);
                return result;
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                AdvanceBoundState(static s => s with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow });
                var cancelled = Result.Failure("Agent was cancelled.");
                completion.TrySetResult(cancelled);
                return cancelled;
            }
            catch (Exception ex)
            {
                AdvanceBoundState(static s => s with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow });
                DefaultAgentLog.AgentRunFailed(_logger, ex, state.SessionId);
                var failed = Result.Failure(ex.Message);
                // #1105: waiters hold a non-generic Task — they cannot observe
                // a Result value, only completion vs fault. Success and cancel
                // complete quietly, so the fault path must too; the failure
                // itself travels via PromptAsync's own return.
                completion.TrySetResult(failed);
                return failed;
            }
            finally
            {
                linkedCts.Dispose();
            }
        }
        finally
        {
            // #91: unbind before releasing the gate — a ResetAbortSource racing
            // the release must see the idle marker, never a stale generation.
            Volatile.Write(ref _activeRunAbortGeneration, -1);
            _runGate.Release();
        }
    }

    /// <summary>
    ///     Inject a steering message into the current run. The message is processed at the next
    ///     safe boundary (mid-turn after tool results, or between turns — Ф2/B2).
    /// </summary>
    /// <param name="message">The message to inject.</param>
    public void Steer(AgentMessage message) => _steering.Enqueue(message);

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
        // #559: `State?.IsRunning != true` used to be the null-tolerant spelling of
        // "not bound, or bound and idle" — both answer the same question here, so the
        // single pattern covers the absent case explicitly instead of leaning on `?.`.
        if (_state is not { IsRunning: true })
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
        if (_state is { } current && !string.Equals(current.SessionId, session.Id, StringComparison.Ordinal))
        {
            // [G4]: the steering inbox outlives rebinds; stale messages authored
            // for the previous session are dropped, never drained into the new one.
            int dropped = _steering.DrainStale();

            if (dropped > 0)
            {
                DefaultAgentLog.StaleSteeringDropped(_logger, dropped, current.SessionId, session.Id);
            }
        }

        _state = AgentState.Idle(session.Id, agent);
    }

    /// <summary>
    ///     Release all resources held by this agent: event-bus subscription, abort source,
    ///     run gate, steering channel.
    /// </summary>
    public void Dispose()
    {
        _eventBusSubscription?.Dispose();
        _abortSource.Dispose();
        _runGate.Dispose();
        _steering.Complete();
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

    // rop-final-mile L6 / §4.6: the old shape unwrapped Results into exceptions
    // only for PromptAsync's catch to re-wrap them into a Failure — one Bind
    // railway replaces both throw bridges.
    // #1510: the last bridge is gone too. The load failure is returned as the
    // Result it already is, so the run boundary holds NO exception of its own
    // making — the catch below is now only there for the agent loop, which can
    // throw.
    // The chain itself is returned to the caller; the one await is inside the
    // explicit lambda below and continues on the thread pool (ConfigureAwait).
    private Task<Result<ISessionContext>> LoadSessionContextAsync(string sessionId, CancellationToken ct)
        => _sessionStore.GetAsync(sessionId, ct)
            .Bind(session => _sessionStore.GetMessagesAsync(sessionId, ct)
                // The context seeds its running usage/cost total from the store,
                // which is an async read — hence Bind over an explicitly typed
                // async lambda instead of the old sync Map.
                .Bind(async (IReadOnlyList<AgentMessage> messages) =>
                    Result.Success<ISessionContext>(await DefaultSessionContext.CreateAsync(
                        session, messages, _sessionStore, _steering.Channel, _eventBus, ct)
                        .ConfigureAwait(false))));

}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="DefaultAgent" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class DefaultAgentLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Agent {Agent} is running; prompt routed to steering queue")]
    public static partial void PromptRoutedToSteering(ILogger logger, string agent);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to persist user message {MessageId} for session {SessionId}: {Error}")]
    public static partial void PersistUserMessageFailed(ILogger logger, string messageId, string sessionId, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Agent run failed: session={SessionId}")]
    public static partial void AgentRunFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Dropped {Count} stale steering message(s) while rebinding agent from session {OldSession} to {NewSession}")]
    public static partial void StaleSteeringDropped(ILogger logger, int count, string oldSession, string newSession);

    // #1510: the session-context load failure is a Result, not an exception, so it
    // is logged without one. The old path logged it as a run failure, which
    // attached the exception the bridge had just manufactured.
    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to load session context for session {SessionId}: {Error}")]
    public static partial void LoadSessionContextFailed(ILogger logger, string sessionId, string error);

}
