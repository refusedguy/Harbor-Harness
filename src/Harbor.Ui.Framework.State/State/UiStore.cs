using Harbor.Abstractions.Events;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     Declarative UI-driven side-effect. Renderers never call <c>IAgent</c>
///     directly; instead they emit effects that the host executes. This keeps the
///     renderer decoupled from <c>Harbor.Core</c> (per the decoupling contract).
/// </summary>
public abstract record TuiEffect
{
    /// <summary>No-op effect (identity).</summary>
    public sealed record None : TuiEffect;

    /// <summary>Submit a user prompt to the agent.</summary>
    /// <param name="Text">The raw prompt text.</param>
    public sealed record PromptAgent(string Text) : TuiEffect;

    /// <summary>Invoke a slash command handler with the raw <c>/command</c> text.</summary>
    /// <param name="Command">The full slash command (including the leading slash).</param>
    public sealed record RunSlash(string Command) : TuiEffect;

    /// <summary>Cancel the running agent and wait for idle.</summary>
    public sealed record AbortAgent : TuiEffect;

    /// <summary>Leave the interactive loop.</summary>
    public sealed record QuitApp : TuiEffect;
}

/// <summary>
///     Executes <see cref="TuiEffect" /> values. Implemented by the composition
///     root (e.g. <c>Harbor.App.Cli</c>) with access to <c>IAgent</c> and the slash handler.
/// </summary>
public interface ITuiEffectRunner
{
    /// <summary>Execute a single effect. May dispatch follow-up events into the store.</summary>
    public void Run(TuiEffect effect);
}

/// <summary>
///     Single source of truth for the interactive UI. Owns the immutable
///     <see cref="UiState" /> and fans transitions out to subscribers. Framework-free
///     (plain <c>event</c>, no reactive library) so it stays in
///     <c>Harbor.Terminal.Abstractions</c> with zero extra dependencies.
/// </summary>
public sealed class UiStore
{
    // §PERF-007 (RESOLVED): previously `lock(_gate)` serialized every dispatch
    // (user input, scroll, agent event) — a bottleneck under heavy streaming
    // (1000+ events/sec). The lock is now replaced with a lock-free CAS loop on
    // the immutable UiState reference. `_state` is marked `volatile` so the
    // initial read in each iteration is an acquire load (interlocked CAS already
    // provides a full barrier on success).
    //
    // §FP-007 (RESOLVED): the old `Transition(Func<UiState,UiState>)` escape
    // hatch is gone — the last callers (TuiEffectHost, SessionManager,
    // SessionSwitcher) now dispatcher UiMsg.AgentStarted / AgentEnded /
    // StatusChanged / AppendLine through the pure reducer instead. Concurrent
    // agents therefore cannot corrupt each other's state via shared mutation:
    // every fold rides the same CAS + UiReducer.Update path.
    private volatile UiState _state;


    /// <summary>Construct a store with the initial (empty) state.</summary>
    public UiStore(UiState? initial = null)
    {
        _state = initial ?? new UiState();
    }

    /// <summary>The current immutable snapshot. Cheap to read; never mutate.</summary>
    public UiState State => _state;

    /// <summary>Raised after every successful <see cref="Dispatch" />.</summary>
    public event EventHandler<UiStateChangedEventArgs>? Changed;

    /// <summary>
    ///     Apply an agent event through the pure reducer and notify subscribers.
    ///     Thread-safe; concurrent dispatches are coalesced via CAS retry.
    /// </summary>
    public void Dispatch(AgentEvent @event)
    {
        UiState original;
        UiState next;
        do
        {
            original = _state; // volatile read
            next = UiReducer.Reduce(original, @event);
            // No-op short-circuit: avoid the event if nothing changed.
            if (ReferenceEquals(original, next))
                return;
        } while (Interlocked.CompareExchange(ref _state, next, original) != original);

        Changed?.Invoke(this, new UiStateChangedEventArgs(next));
    }

    /// <summary>
    ///     The unified TEA dispatch: route any <see cref="UiMsg" /> through the single
    ///     <see cref="UiReducer.Update(UiState, UiMsg)" />, apply the resulting state,
    ///     and return the effect for the host to run. Renderers call this and run the
    ///     returned effect — they never mutate state or call <c>IAgent</c> themselves.
    /// </summary>
    public TuiEffect Dispatch(UiMsg msg)
    {
        UiState original;
        UiState next;
        TuiEffect effect;
        do
        {
            original = _state; // volatile read
            (next, effect) = UiReducer.Update(original, msg);
            // No-op short-circuit: state unchanged, no event.
            if (ReferenceEquals(original, next))
                return effect;
        } while (Interlocked.CompareExchange(ref _state, next, original) != original);

        Changed?.Invoke(this, new UiStateChangedEventArgs(next));
        return effect;
    }

    /// <summary>
    ///     Apply a manual state fold through a reducer function. Private on
    ///     purpose (§FP-007 resolved): every state change must be expressible as a
    ///     <see cref="Dispatch(UiMsg)" /> so concurrent agents in different
    ///     sessions can never bypass the pure reducer. Only the store's own
    ///     convenience wrappers (<see cref="BindSession" />, <see cref="Reset" />) fold directly.
    /// </summary>
    private void Transition(Func<UiState, UiState> reducer)
    {
        UiState original;
        UiState next;
        do
        {
            original = _state; // volatile read
            next = reducer(original);
            if (ReferenceEquals(original, next))
                return;
        } while (Interlocked.CompareExchange(ref _state, next, original) != original);

        Changed?.Invoke(this, new UiStateChangedEventArgs(next));
    }

    /// <summary>Bind session chrome (model/provider/agent) into the state.</summary>
    public void BindSession(string model, string provider, string agentName) => Transition(s => s with { Model = model, Provider = provider, AgentName = agentName });

    /// <summary>Reset to a fresh empty state (e.g. on clear-screen).</summary>
    public void Reset() => Transition(_ => new UiState());
}

/// <summary>Event args carrying the new immutable <see cref="UiState" /> snapshot.</summary>
public sealed class UiStateChangedEventArgs : EventArgs
{
    public UiStateChangedEventArgs(UiState state)
    {
        State = state;
    }

    /// <summary>The new UI snapshot after the transition.</summary>
    public UiState State { get; }
}
