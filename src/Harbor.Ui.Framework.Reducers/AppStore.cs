using Harbor.Abstractions.Events;
using Harbor.Ui.Framework.Reducers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Single source of truth for the application state. Owns the immutable
///     <see cref="AppState" /> and fans transitions out to subscribers.
/// </summary>
public sealed class AppStore
{
    private AppState _state = new();

    /// <summary>The current immutable snapshot. Cheap to read; never mutate.</summary>
    public AppState State => _state;

    /// <summary>Raised after every successful <see cref="Dispatch" />.</summary>
    public event EventHandler<AppState>? StateChanged;

    /// <summary>
    ///     Apply an agent event through the pure reducer and notify subscribers.
    /// </summary>
    public void Dispatch(AgentEvent @event)
    {
        // Fully qualified: this file lives in the Harbor.Ui.Framework.State
        // namespace, which also declares the generic TEA AppReducer (#33/T4).
        // The unqualified name binds to the namespace member, not to the
        // legacy flat-AppState reducer in Harbor.Ui.Framework.Reducers.
        _state = Harbor.Ui.Framework.Reducers.AppReducer.Reduce(@event, _state);
        StateChanged?.Invoke(this, _state);
    }
}
