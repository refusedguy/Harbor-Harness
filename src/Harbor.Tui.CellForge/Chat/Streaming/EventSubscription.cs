using Harbor.Abstractions.Events;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Bus-subscription lifecycle extracted from <see cref="ChatScreenBridge"/>
/// (#172). Auto-subscribe suits fire-and-forget hosts (CE-3 tests); a driven
/// host (CellForge REPL frame loop) passes <c>autoSubscribe: false</c> and
/// pumps events via <see cref="ChatScreenBridge.AcceptAsync"/> so all timeline
/// mutation stays on the render thread.
/// </summary>
internal sealed class EventSubscription : IDisposable
{
    public EventSubscription(IEventBus bus, Func<AgentEvent, CancellationToken, ValueTask> handler, bool autoSubscribe = true)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(handler);
        Subscription = autoSubscribe ? bus.Subscribe(handler) : NoSubscription.Instance;
    }

    public IDisposable Subscription { get; }

    public void Dispose() => Subscription.Dispose();

    private sealed class NoSubscription : IDisposable
    {
        public static readonly NoSubscription Instance = new();
        public void Dispose()
        {
        }
    }
}
