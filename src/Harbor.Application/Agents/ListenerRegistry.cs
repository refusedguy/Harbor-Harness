using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     Thread-safe agent-event listener set extracted from <see cref="DefaultAgent" /> ([G4]).
///     Listeners are snapshotted under the lock and invoked outside it, so a
///     slow listener never blocks subscribe/unsubscribe.
/// </summary>
internal sealed class ListenerRegistry
{
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = new();
    private readonly object _listenersLock = new();

    /// <summary>Subscribe a listener; the returned disposable unsubscribes on dispose.</summary>
    internal IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_listenersLock)
        {
            _listeners.Add(listener);
        }

        return new Unsubscriber(() =>
        {
            lock (_listenersLock)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>
    ///     Dispatch one event to every subscribed listener. A failing listener
    ///     is logged and skipped — it never breaks delivery to the rest.
    /// </summary>
    /// <remarks>
    ///     <paramref name="sessionId" /> is <c>Maybe.None</c> before the agent has been
    ///     initialized (#559). Absence travels as <c>Maybe&lt;string&gt;</c> rather than a null
    ///     sentinel so "unbound" is visible in the signature instead of being a convention.
    /// </remarks>
    internal async Task DispatchAsync(
        AgentEvent evt,
        CancellationToken ct,
        ILogger logger,
        Maybe<string> sessionId)
    {
        // Snapshot listeners under the lock, then iterate the snapshot outside the lock.
        // Previously this allocated a fresh List<T> via ToList() on every published event,
        // which is significant for high-frequency events like MessageUpdateEvent.
        Func<AgentEvent, CancellationToken, ValueTask>[] snapshot;
        lock (_listenersLock)
        {
            int count = _listeners.Count;
            if (count == 0) return;
            snapshot = new Func<AgentEvent, CancellationToken, ValueTask>[count];
            for (int i = 0; i < count; i++)
            {
                snapshot[i] = _listeners[i];
            }
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try
            {
                await snapshot[i](evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Listener failed: session={SessionId}", sessionId.GetValueOrDefault("unbound"));
            }
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _action;
        internal Unsubscriber(Action action)
        {
            _action = action;
        }
        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}
