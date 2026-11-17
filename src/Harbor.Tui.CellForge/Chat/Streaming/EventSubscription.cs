using Harbor.Abstractions.Events;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Bus-subscription lifecycle extracted from <see cref="ChatScreenBridge"/>
/// (#172). Auto-subscribe suits fire-and-forget hosts (CE-3 tests); a driven
/// host (CellForge REPL frame loop) passes <c>autoSubscribe: false</c> and
/// pumps events via <see cref="ChatScreenBridge.AcceptAsync"/> so all timeline
/// mutation stays on the render thread.
/// </summary>
/// <remarks>
///     #2358: this is the single ownership primitive for every renderer bus
///     subscription. The <see cref="IDisposable"/> handle lives in a field of
///     the owning bridge and is released when that owner is disposed, so a
///     subscription never outlives its owner. Disposal is idempotent.
/// </remarks>
internal sealed class EventSubscription : IDisposable
{
    private int _disposed;

    public EventSubscription(IEventBus bus, Func<AgentEvent, CancellationToken, ValueTask> handler, bool autoSubscribe = true)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(bus);
        Harbor.Abstractions.Extensions.Ensure.NotNull(handler);
        Subscription = autoSubscribe ? bus.Subscribe(handler) : NoSubscription.Instance;
    }

    public IDisposable Subscription { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Subscription.Dispose();
        }
    }

    private sealed class NoSubscription : IDisposable
    {
        public static readonly NoSubscription Instance = new();
        public void Dispose()
        {
        }
    }
}
