using Harbor.Abstractions.Events;
namespace Harbor.Tui.Abstractions.State;
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
///     root (e.g. <c>Harbor.Cli</c>) with access to <c>IAgent</c> and the slash handler.
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
///     <c>Harbor.Tui.Abstractions</c> with zero extra dependencies.
/// </summary>
public sealed class UiStore
{
    private readonly object _gate = new();
    private UiState _state;

    /// <summary>Construct a store with the initial (empty) state.</summary>
    public UiStore(UiState? initial = null)
    {
        _state = initial ?? new UiState();
    }

    /// <summary>The current immutable snapshot. Cheap to read; never mutate.</summary>
    public UiState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised after every successful <see cref="Dispatch" />.</summary>
    public event EventHandler<UiStateChangedEventArgs>? Changed;

    /// <summary>
    ///     Apply an agent event through the pure reducer and notify subscribers.
    ///     Thread-safe; concurrent dispatches are serialized.
    /// </summary>
    public void Dispatch(AgentEvent @event)
    {
        UiState next;
        lock (_gate)
        {
            next = UiReducer.Reduce(_state, @event);
            _state = next;
        }

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
        UiState next;
        TuiEffect effect;
        lock (_gate)
        {
            (next, effect) = UiReducer.Update(_state, msg);
            _state = next;
        }

        Changed?.Invoke(this, new UiStateChangedEventArgs(next));
        return effect;
    }

    /// <summary>
    ///     Apply a manual transition (e.g. user input, bind meta) through a reducer
    ///     function. Prefer <see cref="Dispatch(UiMsg)" /> for new code; this remains
    ///     for the effect runner (<see cref="TuiEffectHost" />), which legitimately
    ///     folds follow-up state as part of running an effect.
    /// </summary>
    public void Transition(Func<UiState, UiState> reducer)
    {
        UiState next;
        lock (_gate)
        {
            next = reducer(_state);
            _state = next;
        }

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
