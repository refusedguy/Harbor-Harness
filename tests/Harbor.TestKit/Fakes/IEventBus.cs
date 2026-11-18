using Harbor.Abstractions.Events;

namespace Harbor.TestKit;

/// <summary>Event bus that records all published events and forwards to subscribers — merged canonical for bridge & assertion tests.</summary>
public sealed class FakeEventBus : IEventBus
{
    private readonly object _lock = new();
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _handlers = [];
    private readonly Dictionary<long, AgentEvent> _inFlightEvents = [];
    private TaskCompletionSource _drained = NewDrainSignal();
    private long _nextEventId;

    public List<AgentEvent> Events { get; } = [];

    /// <summary>
    ///     Events published but not yet delivered to every subscriber. Zero
    ///     means the last in-flight <see cref="PublishAsync" /> has finished;
    ///     that is the fact <see cref="WaitForDrainAsync" /> waits for.
    /// </summary>
    public int UnpublishedEventCount
    {
        get
        {
            lock (_lock) { return _inFlightEvents.Count; }
        }
    }

    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        long id;
        lock (_lock)
        {
            Events.Add(@event);
            id = ++_nextEventId;
            _inFlightEvents.Add(id, @event);
            if (_inFlightEvents.Count == 1)
                _drained = NewDrainSignal();
        }

        try
        {
            List<Func<AgentEvent, CancellationToken, ValueTask>> snapshot;
            lock (_lock) { snapshot = [.. _handlers]; }
            foreach (var handler in snapshot)
            {
                await handler(@event, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _inFlightEvents.Remove(id);
                if (_inFlightEvents.Count == 0)
                    _drained.TrySetResult();
            }
        }
    }

    /// <summary>
    ///     Wait until every event already published has been delivered to all
    ///     subscribers, including events a subscriber published in turn. This
    ///     is the deterministic replacement for a fixed sleep at the test
    ///     seam: no polling, no timer, and no delay once the pump is empty.
    /// </summary>
    /// <param name="timeout">Maximum wait; five seconds by default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">
    ///     The deadline elapsed with events still undelivered. The message
    ///     names those events and the self-deadlock shape that can cause it.
    /// </exception>
    public async Task WaitForDrainAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(limit);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task signal;
            lock (_lock)
            {
                if (_inFlightEvents.Count == 0)
                    return;
                signal = _drained.Task;
            }

            try
            {
                await signal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                int count;
                string[] pending;
                lock (_lock)
                {
                    count = _inFlightEvents.Count;
                    pending = [.. _inFlightEvents.Values.Select(e => e.GetType().Name).Distinct()];
                }

                throw new TimeoutException(
                    $"FakeEventBus did not drain within {limit}: {count} unpublished event(s) " +
                    $"[{string.Join(", ", pending)}]. A subscriber that awaits WaitForDrainAsync " +
                    "inside PublishAsync deadlocks this wait; publish after its handler returns.");
            }
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

    private static TaskCompletionSource NewDrainSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Disposer(Action dispose) : IDisposable
    {
        private readonly Action _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
