using Harbor.Abstractions.Events;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Bridges <see cref="IEventBus" /> agent events into <see cref="AppStore" />,
///     marshalling the dispatch onto the UI thread via <see cref="IDispatcherAdapter" />.
/// </summary>
public sealed class EventBusAppStoreDispatcher : IAsyncDisposable
{
    private readonly IEventBus _eventBus;
    private readonly AppStore _appStore;
    private readonly IDispatcherAdapter _dispatcher;
    private IDisposable? _subscription;

    public EventBusAppStoreDispatcher(IEventBus eventBus, AppStore appStore, IDispatcherAdapter dispatcher)
    {
        _eventBus = eventBus;
        _appStore = appStore;
        _dispatcher = dispatcher;
    }

    /// <summary>Subscribe to agent events and start feeding them into the app store.</summary>
    public void Start()
    {
        _subscription = _eventBus.Subscribe<AgentEvent>(OnAgentEvent);
    }

    private ValueTask OnAgentEvent(AgentEvent @event, CancellationToken ct)
    {
        _dispatcher.Post(() => _appStore.Dispatch(@event));
        return ValueTask.CompletedTask;
    }

    /// <summary>Unsubscribe from the event bus and release resources.</summary>
    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
    }
}
