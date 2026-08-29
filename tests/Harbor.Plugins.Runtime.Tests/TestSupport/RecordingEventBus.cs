using System.Collections.Concurrent;
using Harbor.Abstractions.Events;

namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     Minimal in-memory <see cref="IEventBus" /> recording published events.
/// </summary>
public sealed class RecordingEventBus : IEventBus
{
    private readonly ConcurrentQueue<AgentEvent> _events = new();

    /// <summary>Events published so far, in order.</summary>
    public IReadOnlyList<AgentEvent> Events => _events.ToArray();

    /// <summary>Events of type <typeparamref name="T" /> published so far.</summary>
    public IReadOnlyList<T> Of<T>() where T : AgentEvent =>
        _events.OfType<T>().ToArray();

    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _events.Enqueue(@event);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) => new NoopDisposable();

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent
        => new NoopDisposable();

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => Events.Take(maxEvents).ToArray();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}