using Harbor.Abstractions.Events;

namespace Harbor.TestKit;

/// <summary>Event bus that records all published events and forwards to subscribers — merged canonical for bridge & assertion tests.</summary>
public sealed class FakeEventBus : IEventBus
{
    private readonly object _lock = new();
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _handlers = [];
    public List<AgentEvent> Events { get; } = [];

    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        lock (_lock) { Events.Add(@event); }
        List<Func<AgentEvent, CancellationToken, ValueTask>> snapshot;
        lock (_lock) { snapshot = [.. _handlers]; }
        foreach (var handler in snapshot)
        {
            await handler(@event, ct).ConfigureAwait(false);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        lock (_lock) { _handlers.Add(handler); }
        return new Disposer(() =>
        {
            lock (_lock) { _handlers.Remove(handler); }
        });
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent
        => Subscribe((e, ct) => e is TEvent typed ? handler(typed, ct) : ValueTask.CompletedTask);

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];

    private sealed class Disposer(Action dispose) : IDisposable
    {
        private readonly Action _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
