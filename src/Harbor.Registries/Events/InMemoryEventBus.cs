using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Abstractions.Events;
/// <summary>
///     In-memory pub/sub event bus. Implements Observer pattern (GOF).
///     Thread-safe. Bounded scrollback backed by a fixed-capacity ring
///     buffer of pre-allocated slots for late-attaching subscribers.
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
///         TUI freeze under heavy event traffic.
///     </para>
///     <para>
///         <b>B7perf (§PERF):</b> scrollback was subsequently an
///         <c>ImmutableArray</c> CAS-appended per publish, which allocated a
///         fresh builder + copy (~8 KB at capacity 1000) on EVERY publish once
///         the buffer reached capacity — even with zero subscribers attached.
///         Scrollback is now a fixed-capacity <c>AgentEvent[]</c> ring
///         allocated once in the constructor: publishing OVERWRITES the oldest
///         slot in place (events are immutable records, so sharing slots is
///         safe) and allocates nothing. Readers receive a point-in-time copy.
///     </para>
///     <para>
///         Performance characteristics:
///         <list type="bullet">
///             <item>
///                 <see cref="PublishAsync" /> fast path: when no middleware is
///                 registered, scrollback is disabled (<c>maxScrollback &lt;= 0</c>)
///                 and there are zero subscribers, the method returns before
///                 touching any collection — zero allocation, synchronous
///                 completion. Otherwise: lock-free snapshot read of
///                 subscriptions, one in-place slot write under a short lock
///                 for scrollback, and a pooled buffer for dead-subscriber
///                 collection.
///             </item>
///             <item>
///                 Subscribe/Unsubscribe: lock-free atomic update of an
///                 <see cref="ImmutableArray{T}" />.
///             </item>
///             <item>
///                 Scrollback: <see cref="GetScrollback" /> copies the requested
///                 tail under the scrollback lock into an exact-size array —
///                 no state mutation, no blocking, repeatable reads.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ILogger<InMemoryEventBus> _logger;

    /// <summary>
    ///     Maximum number of events retained in scrollback. Zero disables
    ///     scrollback retention entirely (publishes skip the ring buffer).
    /// </summary>
    private readonly int _maxScrollback;

    /// <summary>
    ///     Middleware pipeline. Evaluated in registration order before scrollback
    ///     and fan-out. Empty (zero-alloc) when no middleware is registered.
    /// </summary>
    private readonly IReadOnlyList<IEventBusMiddleware> _middlewares = Array.Empty<IEventBusMiddleware>();

    /// <summary>
    ///     Pre-allocated scrollback slots. Fixed capacity
    ///     (<see cref="_maxScrollback" />); entries are overwritten oldest-first
    ///     and never reallocated, so steady-state publishing allocates nothing.
    ///     Events are immutable records, so handing out references after the
    ///     slot is overwritten is safe (readers snapshot under the lock).
    /// </summary>
    private readonly AgentEvent[] _scrollbackRing;

    /// <summary>Guards <see cref="_scrollbackRing" />, <see cref="_ringHead"/> and <see cref="_ringCount"/>.</summary>
    private readonly object _scrollbackLock = new();

    /// <summary>Index of the OLDEST entry currently held in <see cref="_scrollbackRing"/>.</summary>
    private int _ringHead;

    /// <summary>Number of valid entries in <see cref="_scrollbackRing"/> (≤ <see cref="_maxScrollback"/>).</summary>
    private int _ringCount;

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
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers. Zero or negative disables scrollback.</param>
    public InMemoryEventBus(int maxScrollback = 1000) : this(NullLogger<InMemoryEventBus>.Instance, maxScrollback) { }

    /// <summary>
    ///     Construct an <see cref="InMemoryEventBus" /> with a logger and bounded scrollback buffer.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers. Zero or negative disables scrollback.</param>
    public InMemoryEventBus(ILogger<InMemoryEventBus> logger, int maxScrollback = 1000)
    {
        _logger = logger;
        _maxScrollback = maxScrollback > 0 ? maxScrollback : 0;
        _scrollbackRing = _maxScrollback > 0 ? new AgentEvent[_maxScrollback] : Array.Empty<AgentEvent>();
    }

    /// <summary>
    ///     Construct an <see cref="InMemoryEventBus" /> with a logger, bounded
    ///     scrollback buffer, and a middleware pipeline.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers. Zero or negative disables scrollback.</param>
    /// <param name="middlewares">Middleware pipeline evaluated before scrollback + fan-out.</param>
    public InMemoryEventBus(ILogger<InMemoryEventBus> logger, int maxScrollback, IEnumerable<IEventBusMiddleware> middlewares)
    {
        _logger = logger;
        _maxScrollback = maxScrollback > 0 ? maxScrollback : 0;
        _scrollbackRing = _maxScrollback > 0 ? new AgentEvent[_maxScrollback] : Array.Empty<AgentEvent>();
        _middlewares = middlewares?.ToArray() ?? Array.Empty<IEventBusMiddleware>();
    }

    /// <inheritdoc />
    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _logger.LogDebug("Publishing event: {EventType}", @event.GetType().Name);

        // ── Fast path: nothing to retain, nobody to notify, nothing to filter.
        //    Returns before touching any collection — zero allocation, and the
        //    async state machine completes synchronously (cached task).
        if (_middlewares.Count == 0 && _maxScrollback == 0 && _subscriptions.IsEmpty)
        {
            return;
        }

        // ── Middleware pipeline (BEFORE scrollback + fan-out) ──
        // Dropped events never reach scrollback or subscribers.
        if (_middlewares.Count > 0)
        {
            foreach (var mw in _middlewares)
            {
                try
                {
                    bool continuePipeline = await mw.ProcessAsync(ref @event, ct).ConfigureAwait(false);
                    if (!continuePipeline)
                    {
                        _logger.LogTrace("Event {EventType} dropped by middleware {Middleware}",
                            @event.GetType().Name, mw.Name);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Middleware {Middleware} threw — event dropped", mw.Name);
                    return;
                }
            }
        }

        // 1. Append to scrollback ring buffer (in-place overwrite, short lock).
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
        if (_maxScrollback == 0 || maxEvents <= 0)
        {
            return Array.Empty<AgentEvent>();
        }

        // Copy the requested tail under the lock into an exact-size array.
        // Readers get a stable point-in-time snapshot: repeated calls see the
        // same history (§PERF-008 regression guarantee), and concurrent
        // publishers can never observe a torn view. The allocation happens
        // only on this (cold) diagnostic path — PublishAsync never allocates
        // for scrollback.
        lock (_scrollbackLock)
        {
            if (_ringCount == 0)
            {
                return Array.Empty<AgentEvent>();
            }

            int count = Math.Min(maxEvents, _ringCount);
            var result = new AgentEvent[count];
            int start = (_ringHead + _ringCount - count) % _maxScrollback;
            for (int i = 0; i < count; i++)
            {
                result[i] = _scrollbackRing[(start + i) % _maxScrollback];
            }

            return result;
        }
    }

    /// <summary>
    ///     Overwrite-append an event into the fixed-capacity ring. When the
    ///     buffer is at capacity the oldest slot is reused in place — no
    ///     allocation, ever. The lock scope covers only the index arithmetic
    ///     and the slot write; fan-out and logging stay outside.
    /// </summary>
    private void AppendScrollback(AgentEvent @event)
    {
        if (_maxScrollback == 0)
        {
            return; // scrollback disabled
        }

        lock (_scrollbackLock)
        {
            if (_ringCount < _maxScrollback)
            {
                // Under capacity — fill the next free slot.
                _scrollbackRing[(_ringHead + _ringCount) % _maxScrollback] = @event;
                _ringCount++;
            }
            else
            {
                // At capacity — overwrite the oldest entry and advance the head.
                _scrollbackRing[_ringHead] = @event;
                _ringHead = (_ringHead + 1) % _maxScrollback;
            }
        }
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
