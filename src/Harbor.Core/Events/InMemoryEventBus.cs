using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
namespace Harbor.Abstractions.Events;
/// <summary>
///     In-memory pub/sub event bus. Implements Observer pattern (GOF).
///     Thread-safe. Bounded scrollback buffer (backed by <see cref="Channel{T}" />) for late-attaching subscribers.
///     Performance characteristics:
///     - PublishAsync: lock-free snapshot read (zero alloc on the common path),
///     pooled buffer for dead-subscriber collection.
///     - Subscribe/Unsubscribe: lock-free atomic update of an <see cref="ImmutableArray{T}" />.
///     - Scrollback: bounded <see cref="Channel{T}" /> with DropOldest semantics.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly int _maxScrollback;
    private readonly Channel<AgentEvent> _scrollback;
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
    public InMemoryEventBus(int maxScrollback = 1000)
    {
        _maxScrollback = maxScrollback;
        _scrollback = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(maxScrollback)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <inheritdoc />
    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // 1. Add to scrollback channel (drop oldest if full)
        _scrollback.Writer.TryWrite(@event);

        // 2. Lock-free snapshot — no List copy, no lock contention
        var snapshot = _subscriptions;
        if (snapshot.IsEmpty)
        {
            return;
        }

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
                catch (Exception)
                {
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
        var all = _scrollback.Reader.ReadAllAsync(CancellationToken.None)
            .ToBlockingEnumerable()
            .TakeLast(maxEvents)
            .ToList();
        return all;
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
