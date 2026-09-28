using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     Declarative UI-driven side-effect. Renderers never call <c>IAgent</c>
///     directly; instead they emit effects that the host executes. This keeps the
///     renderer decoupled from the Application layer (per the decoupling contract).
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

    /// <summary>
    ///     Make a session the active one — the tab-strip's "activate" contract
    ///     (#388). The host routes it to the existing session-switch path
    ///     (<c>ISessionManager.OpenSessionAsync</c>); the reducer itself never
    ///     resolves DI, it only asks (see <see cref="ChatAppReducer.ActivateTab" />).
    ///     Pure state-only hosts that never switch sessions may ignore it.
    /// </summary>
    /// <param name="SessionId">The session the host must open.</param>
    public sealed record ActivateSession(SessionId SessionId) : TuiEffect;

    /// <summary>
    ///     Ask the host to open or switch a session — the tab-strip's "open tab"
    ///     contract (#389, raised by <see cref="ChatAction.OpenTab" />).
    ///     Deliberately argument-free: <i>which</i> session to open is a picker
    ///     decision the reducer cannot make, so it only asks and the host answers
    ///     with its own switch UI. Hosts with no picker wired may ignore it —
    ///     same contract as <see cref="ActivateSession" />.
    /// </summary>
    public sealed record RequestOpenSession : TuiEffect;
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
    // hatch is gone — every fold (session chrome, resets, agent events, host
    // messages) rides the same CAS + AppReducer.Update path via
    // Dispatch(AppMsg). Concurrent agents therefore cannot corrupt each
    // other's state via shared mutation.
    private volatile UiState _state;

    // #491: the subscriber set is a flat, per-subscriber snapshot
    // that is rebuilt on subscription changes and read lock-free on every
    // notification. It used to be rebuilt per Notify from
    // `Changed.GetInvocationList().Cast<EventHandler<…>>()` — a `Delegate[]`
    // plus a `Cast` iterator on the hottest path in the TUI, where the store
    // pays for delivery isolation on every dispatch instead of on every
    // subscribe/unsubscribe. The array is replaced, never mutated, so the
    // snapshot semantics Notify documents are unchanged.
    private readonly object _subscribersGate = new();
    private readonly List<EventHandler<UiStateChangedEventArgs>> _subscribers = [];
    private volatile EventHandler<UiStateChangedEventArgs>[] _delivery = Array.Empty<EventHandler<UiStateChangedEventArgs>>();

    /// <summary>Construct a store with the initial (empty) state.</summary>
    public UiStore(UiState? initial = null)
    {
        _state = initial ?? new UiState();
    }

    /// <summary>The current immutable snapshot. Cheap to read; never mutate.</summary>
    public UiState State => _state;

    /// <summary>
    ///     Raised after every successful <see cref="Dispatch" />.
    ///     Delivery is synchronous on the dispatching thread: subscribers must
    ///     consume <see cref="UiStateChangedEventArgs.State" /> (and drop stale
    ///     revisions via <see cref="UiStateChangedEventArgs.IsStale" />) — never
    ///     re-read the store and never mutate state. A throwing subscriber is
    ///     isolated: remaining subscribers are still notified.
    /// </summary>
    /// <remarks>
    ///     Backed by an explicit delivery set rather than a field-like event,
    ///     because <see cref="Notify" /> must not pay for the delivery set on
    ///     every dispatch. Semantics match a field-like <c>event</c>: handlers
    ///     are called in subscription order, the same handler may be registered
    ///     twice, and each unsubscribe drops one (the last) registration. One
    ///     deliberate difference: a pre-composed multicast delegate passed to
    ///     <c>+=</c> stays a single registration instead of being flattened, so
    ///     a throw inside it skips the rest of that delegate's own list — no
    ///     call site in the repo does this, and flattening it here would need
    ///     the <c>GetInvocationList</c> this issue removed.
    /// </remarks>
    public event EventHandler<UiStateChangedEventArgs>? Changed
    {
        // Subscribe/unsubscribe are cold; the delivery set is what dispatch pays for.
        add
        {
            if (value is null)
                return;
            lock (_subscribersGate)
            {
                _subscribers.Add(value);
                PublishDeliveryLocked();
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_subscribersGate)
            {
                // Last match, mirroring Delegate.Remove for the single-cast
                // handlers every renderer subscribes.
                for (int i = _subscribers.Count - 1; i >= 0; i--)
                {
                    if (!_subscribers[i].Equals(value))
                        continue;
                    _subscribers.RemoveAt(i);
                    break;
                }

                PublishDeliveryLocked();
            }
        }
    }

    /// <summary>
    ///     Replace the delivery snapshot. Callers must hold
    ///     <c>_subscribersGate</c>; the write is a single volatile publish, so a
    ///     concurrent Notify sees either the old or the new set.
    /// </summary>
    private void PublishDeliveryLocked() =>
        _delivery = _subscribers.Count == 0
            ? Array.Empty<EventHandler<UiStateChangedEventArgs>>()
            : _subscribers.ToArray();

    /// <summary>
    ///     The unified TEA dispatch — the single entry point for every state
    ///     change: route any <see cref="AppMsg" /> (generic) or
    ///     <see cref="ChatAppMsg" /> (Harbor chat) through the composed
    ///     <see cref="ChatAppReducer.Update" /> — which is
    ///     <see cref="AppReducer.Update" /> plus the chat extension — apply the
    ///     resulting state, and return the effect for the host to run. Renderers
    ///     call this and run the returned effect — they never mutate state or call
    ///     <c>IAgent</c> themselves.
    /// </summary>
    public TuiEffect Dispatch(AppMsg msg)
    {
        UiState original;
        UiState next;
        TuiEffect effect;
        do
        {
            original = _state; // volatile read
            (next, effect) = ChatAppReducer.Update(original, msg);
            // No-op short-circuit: state unchanged, no event.
            if (ReferenceEquals(original, next))
                return effect;
            // #491: one copy per dispatch. The reducer handed back
            // a snapshot it just built, so the revision is stamped on that
            // instance instead of cloning it — `next with { Revision = … }`
            // allocated a second full UiState per message. Stamping happens
            // *before* the CAS below, so the instance is still unreachable and
            // no reader can observe the pre-stamp revision. A CAS loser throws
            // its stamped instance away with the rest of the iteration.
            next.SetRevision(original.Revision + 1);
        } while (Interlocked.CompareExchange(ref _state, next, original) != original);

        Notify(next);
        return effect;
    }

    /// <summary>
    ///     Fan-out to <see cref="Changed" /> subscribers. The delegate array is
    ///     snapshotted once (a volatile read of the pre-built delivery set), so
    ///     concurrent subscribe/unsubscribe never tears the delivery set; each
    ///     subscriber runs in its own try/catch so one failing renderer cannot
    ///     starve the rest or fail the dispatch. Still synchronous on the
    ///     dispatching thread — the frame loop owns marshaling (consume
    ///     <c>e.State</c>, drop stale via
    ///     <see cref="UiStateChangedEventArgs.IsStale" />).
    /// </summary>
    private void Notify(UiState next)
    {
        var delivery = _delivery; // volatile read
        if (delivery.Length == 0)
            return; // nobody listening: no EventArgs, no fan-out

        // One allocation per notification, and only when a subscriber can
        // actually observe it. It cannot be pooled or cached: subscribers may
        // keep the args and read State later (dropping stale revisions on their
        // own schedule), so each notification needs its own instance.
        var args = new UiStateChangedEventArgs(next);
        for (int i = 0; i < delivery.Length; i++)
        {
            try
            {
                delivery[i](this, args);
            }
            catch
            {
                // Isolated per subscriber (issue #81): notification fan-out
                // must survive a failing renderer. No logging here — the store
                // is framework-free by design; renderers own error reporting.
            }
        }
    }
}

/// <summary>Event args carrying the new immutable <see cref="UiState" /> snapshot.</summary>
public sealed class UiStateChangedEventArgs : EventArgs
{
    public UiStateChangedEventArgs(UiState state)
    {
        State = state;
    }

    /// <summary>The new UI snapshot after the transition. Consume this — never re-read the store.</summary>
    public UiState State { get; }

    /// <summary>
    ///     Monotonic revision of <see cref="State" /> (issue #94). Every
    ///     successful <see cref="UiStore" /> transition bumps it by exactly one.
    /// </summary>
    public long Revision => State.Revision;

    /// <summary>
    ///     Whether this notification is stale relative to an already-applied one.
    ///     CAS success and event delivery are not atomic across threads, so a
    ///     subscriber that applied revision N must ignore any notification with
    ///     <c>Revision &lt;= N</c> instead of rewinding visible state.
    /// </summary>
    /// <param name="lastAppliedRevision">Revision of the last applied notification.</param>
    /// <returns>True when this notification must be dropped.</returns>
    public bool IsStale(long lastAppliedRevision) => Revision <= lastAppliedRevision;
}
