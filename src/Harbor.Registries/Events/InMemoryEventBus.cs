using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Abstractions.Events;
/// <summary>
///     In-memory pub/sub event bus. Implements Observer pattern (GOF).
///     Thread-safe. Bounded scrollback ring buffer (CAS-updated
///     <see cref="ImmutableQueue{T}" /> / <see cref="ImmutableArray{T}" />) for
///     late-attaching subscribers.
/// </summary>
/// <remarks>
///     <para>
///         <b>Architecture audit v2 — §PERF-008 (RESOLVED):</b> the previous
///         implementation backed scrollback with a bounded
///         <see cref="System.Threading.Channels.Channel{T}" /> and drained it on
///         every <see cref="GetScrollback" /> call via
///         <c>ReadAllAsync().ToBlockingEnumerable()</c>. That had two correctness
///         bugs: (1) the channel was emptied after a single late subscriber
///         read it, so subsequent late subscribers saw nothing; (2) the
///         blocking enumeration synchronously blocked the calling thread — a
///         TUI freeze under heavy event traffic. The new implementation keeps
///         the scrollback in an immutable ring buffer updated atomically via
///         <see cref="ImmutableInterlocked" />, so reads never mutate state and
///         never block.
///     </para>
///     <para>
///         Performance characteristics:
///         <list type="bullet">
///             <item>
///                 <see cref="PublishAsync" />: lock-free snapshot read
///                 (zero alloc on the common path), pooled buffer for
///                 dead-subscriber collection. Scrollback append is a CAS retry
///                 on an <see cref="ImmutableArray{T}" />; in the steady state
///                 (under capacity) there is no allocation.
///             </item>
///             <item>
///                 Subscribe/Unsubscribe: lock-free atomic update of an
///                 <see cref="ImmutableArray{T}" />.
///             </item>
///             <item>
///                 Scrollback: <see cref="GetScrollback" /> is a single
///                 volatile snapshot read + a slice; zero state mutation, no
///                 blocking.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ILogger<InMemoryEventBus> _logger;
    private readonly int _maxScrollback;

    /// <summary>
    ///     Scrollback ring buffer. Holds the most recent
    ///     <see cref="_maxScrollback" /> events in publication order. Updated
    ///     via
    ///     <see cref="ImmutableInterlocked.Update{T}(ref ImmutableArray{T}, Func{ImmutableArray{T}, ImmutableArray{T}})" />
    ///     so concurrent publishers never corrupt the buffer. Reads take a
    ///     single volatile snapshot (no copy, no blocking).
    /// </summary>
    private ImmutableArray<AgentEvent> _scrollback = ImmutableArray<AgentEvent>.Empty;

    /// <summary>
    ///     Subscriptions collection. <see cref="ImmutableArray{T}" /> gives us O(1) lock-free
    ///     snapshot reads with zero allocation; mutations use <see cref="ImmutableInterlocked" />
    ///     for atomic CAS-based updates.
    /// </summary>
    private ImmutableArray<Subscription> _subscriptions = ImmutableArray<Subscription>.Empty;

    /// <summary>
    ///     Construct an <see cref="InMemoryEventBus" /> with a bounded scrollback buffer of the
    ///     supplied capacity.
    /// </summary>
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers.</param>
    public InMemoryEventBus(int maxScrollback = 1000) : this(NullLogger<InMemoryEventBus>.Instance, maxScrollback) { }

    /// <summary>
    ///     Construct an <see cref="InMemoryEventBus" /> with a logger and bounded scrollback buffer.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers.</param>
    public InMemoryEventBus(ILogger<InMemoryEventBus> logger, int maxScrollback = 1000)
    {
        _logger = logger;
        _maxScrollback = maxScrollback > 0 ? maxScrollback : 1;
    }

    /// <inheritdoc />
    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _logger.LogDebug("Publishing event: {EventType}", @event.GetType().Name);

        // 1. Append to scrollback ring buffer (lock-free CAS).
        AppendScrollback(@event);

        // 2. Lock-free snapshot — no List copy, no lock contention
        var snapshot = _subscriptions;
        if (snapshot.IsEmpty)
        {
            _logger.LogTrace("No subscribers for event {EventType}", @event.GetType().Name);
            return;
        }

        _logger.LogTrace("Publishing to {SubscriberCount} subscribers", snapshot.Length);

        // 3. Fan-out to subscribers; collect failures in a pooled array to avoid
        //    allocating a List in the common case where nothing throws.
        Subscription[]? dead = null;
        int deadCount = 0;
        try
        {
            int snapshotLength = snapshot.Length;
            for (int i = 0; i < snapshotLength; i++)
            {
                var sub = snapshot[i];
                try
                {
                    await sub.Handler(@event, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subscriber threw exception — removing dead subscriber");
                    if (dead is null)
                    {
                        dead = ArrayPool<Subscription>.Shared.Rent(snapshotLength);
                    }
                    dead[deadCount++] = sub;
                }
            }

            if (deadCount > 0)
            {
                // dead is guaranteed non-null here: it's assigned the first time
                // any subscriber throws (which is the only way deadCount can exceed 0).
                RemoveDeadSubscriptions(dead!, deadCount);
            }
        }
        finally
        {
            if (dead is not null)
            {
                // Clear references so the pooled array doesn't keep the Subscription
                // (and indirectly the handler delegate) alive after return.
                Array.Clear(dead, 0, deadCount);
                ArrayPool<Subscription>.Shared.Return(dead);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        var sub = new Subscription(handler);
        ImmutableInterlocked.Update(ref _subscriptions, static (arr, s) => arr.Add(s), sub);

        return new Unsubscriber(() =>
        {
            ImmutableInterlocked.Update(ref _subscriptions, static (arr, s) => arr.Remove(s), sub);
        });
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent
    {
        return Subscribe(async (evt, ct) =>
        {
            if (evt is TEvent typed)
            {
                await handler(typed, ct).ConfigureAwait(false);
            }
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents)
    {
        // Architecture audit v2 §PERF-008 (RESOLVED): a single volatile snapshot
        // of the immutable ring buffer. No mutation of bus state, no blocking —
        // subsequent late subscribers see the same history. The slice is
        // materialized lazily: in the common case (maxEvents >= buffer size)
        // we return the snapshot as-is; only when truncation is requested do
        // we allocate a trimmed copy.
        var snapshot = _scrollback;
        if (snapshot.IsEmpty)
        {
            return Array.Empty<AgentEvent>();
        }

        if (maxEvents >= snapshot.Length)
        {
            return snapshot;
        }

        if (maxEvents <= 0)
        {
            return Array.Empty<AgentEvent>();
        }

        // Take the tail (most recent) — matches the prior "keep last maxEvents" contract.
        int start = snapshot.Length - maxEvents;
        var tail = new AgentEvent[maxEvents];
        for (int i = 0; i < maxEvents; i++)
        {
            tail[i] = snapshot[start + i];
        }
        return tail;
    }

    /// <summary>
    ///     Append an event to the scrollback ring buffer atomically. When the
    ///     buffer is at capacity, the oldest entry is dropped. The update uses
    ///     <see
    ///         cref="ImmutableInterlocked.Update{T, TState}(ref ImmutableArray{T}, Func{ImmutableArray{T}, TState, ImmutableArray{T}}, TState)" />
    ///     so concurrent publishers never lose an event to a stale read.
    /// </summary>
    private void AppendScrollback(AgentEvent @event)
    {
        // Inline CAS loop avoids the closure allocation that
        // ImmutableInterlocked.Update would incur by capturing @event.
        ImmutableArray<AgentEvent> original;
        ImmutableArray<AgentEvent> updated;
        do
        {
            original = _scrollback;
            if (original.Length < _maxScrollback)
            {
                // Under capacity — just append.
                updated = original.Add(@event);
            }
            else
            {
                // At capacity — drop the oldest entry. Builder is reused for
                // O(n) construction (one alloc) instead of RemoveAt(0)+Add
                // (two O(n) operations on the immutable spine).
                var builder = ImmutableArray.CreateBuilder<AgentEvent>(original.Length);
                // Skip index 0 (the oldest), copy the rest, then append the new event.
                for (int i = 1; i < original.Length; i++)
                {
                    builder.Add(original[i]);
                }
                builder.Add(@event);
                updated = builder.MoveToImmutable();
            }
        } while (ImmutableInterlocked.InterlockedCompareExchange(ref _scrollback, updated, original) != original);
    }

    private void RemoveDeadSubscriptions(Subscription[] dead, int deadCount)
    {
        // Inline CAS loop avoids the closure allocation that ImmutableInterlocked.Update
        // would incur by capturing `dead`/`deadCount`.
        ImmutableArray<Subscription> original;
        ImmutableArray<Subscription> updated;
        do
        {
            original = _subscriptions;
            updated = original;
            for (int i = 0; i < deadCount; i++)
            {
                updated = updated.Remove(dead[i]);
            }
        } while (ImmutableInterlocked.InterlockedCompareExchange(ref _subscriptions, updated, original) != original);
    }

    /// <summary>
    ///     Internal subscriber record. Sequential layout for cache-friendly iteration.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private sealed class Subscription
    {
        public Func<AgentEvent, CancellationToken, ValueTask> Handler { get; }

        public Subscription(Func<AgentEvent, CancellationToken, ValueTask> handler)
        {
            Handler = handler;
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _action;

        public Unsubscriber(Action action)
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
