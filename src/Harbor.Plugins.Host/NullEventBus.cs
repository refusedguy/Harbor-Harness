using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Host;

/// <summary>
///     No-op <see cref="IEventBus" />. The plugin host runs as a standalone MCP server and has
///     no agent loop to broadcast to — plugins that subscribe to events simply get nothing.
/// </summary>
internal sealed class NullEventBus : IEventBus
{
    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
        => Task.CompletedTask;

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
        => NullSubscription.Instance;

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : AgentEvent
        => NullSubscription.Instance;

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => ImmutableArray<AgentEvent>.Empty;

    private sealed class NullSubscription : IDisposable
    {
        public static readonly NullSubscription Instance = new();
        public void Dispose() { }
    }
}
