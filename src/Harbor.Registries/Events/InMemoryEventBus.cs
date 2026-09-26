using System.Collections.Immutable;
using System.Diagnostics;
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
///             <item>
///                 Queue-age instrumentation (#47): every slow-path publish takes
///                 one monotonic submission timestamp
///                 (<see cref="Stopwatch.GetTimestamp" />, allocation-free) and
///                 maintains <see cref="PublishedCount" />,
///                 <see cref="InflightPublishCount" />,
///                 <see cref="OldestPendingAge" /> and
///                 <see cref="MaxDispatchDuration" /> via Interlocked-only updates
///                 (zero GC pressure, no locks). The fast path returns before any
///                 of this, so its zero-alloc claim is unchanged. No
///                 <c>IMetrics</c> dependency: Harbor.Registries may reference
///                 Harbor.Abstractions only, so percentile export stays in the
///                 telemetry layer, which polls these counters.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class InMemoryEventBus : IEventBus
{
    /// <summary>Default per-handler budget (A4): one slow subscriber may hold
    /// the fan-out for at most this long before it is left behind.</summary>
    public static readonly TimeSpan DefaultHandlerBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Consecutive over-budget dispatches before a subscriber is evicted.</summary>
    private const int MaxSlowStrikes = 3;

    /// <summary>Per-dispatch budget; TimeSpan.Zero disables the budget entirely.</summary>
    private readonly TimeSpan _handlerBudget;

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
    ///     Queue-age instrumentation (#47). Updated with
    ///     <see cref="Interlocked" /> / <see cref="Volatile" /> only — no
    ///     locks, no allocations — and read via the public counters below.
    ///     <c>IMetrics</c> is deliberately NOT referenced here:
    ///     Harbor.Registries may depend on Harbor.Abstractions only
    ///     (docs/ARCHITECTURE_LAYERS.md §2), so percentile export stays in
    ///     the telemetry layer, which can poll these counters.
    /// </summary>
    private long _publishedCount;
    private long _inflightPublishCount;
    private long _oldestEnqueuedTicks;
    private long _maxDispatchTicks;

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
        : this(logger, maxScrollback, handlerBudget: DefaultHandlerBudget)
    {
    }

    /// <summary>
    ///     Construct an <see cref="InMemoryEventBus" /> with a logger, bounded
    ///     scrollback buffer, and a middleware pipeline.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers. Zero or negative disables scrollback.</param>
    /// <param name="middlewares">Middleware pipeline evaluated before scrollback + fan-out.</param>
    public InMemoryEventBus(ILogger<InMemoryEventBus> logger, int maxScrollback, IEnumerable<IEventBusMiddleware> middlewares)
        : this(logger, maxScrollback, DefaultHandlerBudget, middlewares)
    {
    }

    /// <summary>
    ///     Full constructor (A4 backpressure): per-subscriber dispatch budget.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxScrollback">Scrollback capacity; zero or negative disables scrollback.</param>
    /// <param name="handlerBudget">
    ///     Per-subscriber dispatch budget. A handler exceeding it is no longer
    ///     awaited by the publisher (its task stays observed), the strike
    ///     counter increments, and after <c>MaxSlowStrikes</c> consecutive
    ///     strikes the subscriber is evicted. <see cref="Timeout.InfiniteTimeSpan"/>-like
    ///     semantics via <see cref="TimeSpan.Zero"/> (budget disabled).
    /// </param>
    public InMemoryEventBus(ILogger<InMemoryEventBus> logger, int maxScrollback, TimeSpan handlerBudget)
        : this(logger, maxScrollback, handlerBudget, middlewares: null)
    {
    }

    /// <summary>Full constructor with middleware (A4 backpressure).</summary>
    public InMemoryEventBus(
        ILogger<InMemoryEventBus> logger,
        int maxScrollback,
        TimeSpan handlerBudget,
        IEnumerable<IEventBusMiddleware>? middlewares)
    {
        _logger = logger;
        _maxScrollback = maxScrollback > 0 ? maxScrollback : 0;
        _handlerBudget = handlerBudget < TimeSpan.Zero ? TimeSpan.Zero : handlerBudget;
        _scrollbackRing = _maxScrollback > 0 ? new AgentEvent[_maxScrollback] : Array.Empty<AgentEvent>();
        _middlewares = middlewares?.ToArray() ?? Array.Empty<IEventBusMiddleware>();
    }

    /// <inheritdoc />
    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // ── Fast path: nothing to retain, nobody to notify, nothing to filter.
        //    Returns before touching any collection — zero allocation, and the
        //    async state machine completes synchronously (cached task).
        //    NOTE (#97 per-delta): the Debug log below must stay AFTER this
        //    check. LogDebug evaluates @event.GetType().Name + the params
        //    object[] eagerly even when Debug is off, which allocated on EVERY
        //    publish (one per streaming delta) and broke the zero-alloc claim.
        //    Queue-age instrumentation (#47) likewise lives strictly below
        //    this line so the fast path stays untouched.
        if (_middlewares.Count == 0 && _maxScrollback == 0 && _subscriptions.IsEmpty)
        {
            return;
        }

        // ── Queue-age envelope (#47): one monotonic submission timestamp per
        //    publish. Stopwatch.GetTimestamp is allocation-free; the
        //    in-flight/oldest bookkeeping in TrackEnqueued/TrackDequeued is
        //    Interlocked-only, so this adds time (~50ns) but zero GC pressure.
        //    The finally below guarantees the in-flight count resets even if
        //    a middleware or fan-out path below ever throws past its guards.
        long enqueuedTicks = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _publishedCount);
        Interlocked.Increment(ref _inflightPublishCount);
        TrackEnqueued(enqueuedTicks);
        try
        {
            // Guarded with IsEnabled — same hot-path pattern as AgentLoop
            // (per-stream-event Trace guard) and ToolDispatcher (GetRawText guard):
            // avoids the params-array + GetType().Name allocation when Debug is off.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Publishing event: {EventType}", @event.GetType().Name);
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
                            // IsEnabled guard (#47): the drop path is cold, but
                            // the params array + GetType().Name evaluate eagerly
                            // even when Trace is off — the same pitfall as the
                            // #97 Debug log above.
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.LogTrace("Event {EventType} dropped by middleware {Middleware}",
                                    @event.GetType().Name, mw.Name);
                            }

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
                // IsEnabled guard (#47): this is THE benchmarked path
                // (EventBusBenchmark, 0 subscribers with scrollback enabled) —
                // without the guard every publish allocates a params object[]
                // + the GetType().Name string even when Trace is off.
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("No subscribers for event {EventType}", @event.GetType().Name);
                }

                return;
            }

            // IsEnabled guard (#47): same eager params-array + int boxing
            // saving as the empty-snapshot branch above.
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Publishing to {SubscriberCount} subscribers", snapshot.Length);
            }

            // 3. Fan-out to subscribers under the A4 per-handler budget: a handler
            //    that exceeds its slice is no longer awaited by THIS publisher
            //    (the orphaned task stays observed via a fault-logging
            //    continuation), its slow-strike counter increments, and after
            //    MaxSlowStrikes consecutive strikes the subscriber is evicted.
            //    Fast handlers — the overwhelmingly common case — complete
            //    synchronously and keep the exact publish-then-observe contract.
            Subscription[]? dead = null;
            int deadCount = 0;
            try
            {
                int snapshotLength = snapshot.Length;
                bool budgetEnabled = _handlerBudget > TimeSpan.Zero;
                // No linked registration when the outer token can never fire:
                // a fresh CTS behaves identically (CancelAfter still applies)
                // and skips the linked-cancellation allocation (#47).
                using CancellationTokenSource? budgetCts = budgetEnabled
                    ? (ct.CanBeCanceled
                        ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                        : new CancellationTokenSource())
                    : null;

                void MarkDead(Subscription sub)
                {
                    if (dead is null)
                    {
                        dead = ArrayPool<Subscription>.Shared.Rent(snapshotLength);
                    }

                    dead[deadCount++] = sub;
                }

                async ValueTask RecordSlowStrikeAsync(Subscription sub, Task handlerTask)
                {
                    int strikes = sub.Strike();
                    _logger.LogWarning(
                        "Subscriber exceeded its {Budget}ms dispatch budget ({Strikes}/{Max} strikes) — continuing without it",
                        _handlerBudget.TotalMilliseconds, strikes, MaxSlowStrikes);
                    if (strikes >= MaxSlowStrikes)
                    {
                        MarkDead(sub);
                    }

                    // Keep the orphaned handler observed so late faults are never
                    // lost (it may still be running against a stale event).
                    try { await handlerTask.ConfigureAwait(false); }
                    catch (OperationCanceledException oce)
                    {
                        _logger.LogDebug(oce, "Orphaned slow-subscriber handler cancelled with its slice");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Orphaned slow-subscriber handler faulted");
                    }
                }

                for (int i = 0; i < snapshotLength; i++)
                {
                    var sub = snapshot[i];
                    try
                    {
                        if (!budgetEnabled)
                        {
                            await sub.Handler(@event, ct).ConfigureAwait(false);
                            sub.ResetSlowStrikes();
                            continue;
                        }

                        budgetCts!.CancelAfter(_handlerBudget);
                        ValueTask dispatch = sub.Handler(@event, budgetCts.Token);
                        if (dispatch.IsCompletedSuccessfully)
                        {
                            sub.ResetSlowStrikes();
                            continue;
                        }

                        Task handlerTask = dispatch.AsTask();
                        if (!ct.CanBeCanceled)
                        {
                            // The infinite-delay branch below could never win this
                            // race (its only completion source is ct), so awaiting
                            // the handler directly is exactly equivalent — and
                            // skips a Timer + Task allocation per async-incomplete
                            // dispatch (#47).
                            try
                            {
                                await handlerTask.ConfigureAwait(false);
                                sub.ResetSlowStrikes();
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                // The handler hit the budget cancellation from INSIDE
                                // its own body — an over-budget strike, not a death.
                                await RecordSlowStrikeAsync(sub, Task.CompletedTask).ConfigureAwait(false);
                            }

                            continue;
                        }

                        Task winner = await Task.WhenAny(handlerTask, Task.Delay(Timeout.InfiniteTimeSpan, ct))
                            .ConfigureAwait(false);
                        if (winner != handlerTask)
                        {
                            // Publisher slice elapsed while the handler still runs:
                            // leave it behind (observed), count the strike.
                            await RecordSlowStrikeAsync(sub, handlerTask).ConfigureAwait(false);
                            continue;
                        }

                        try
                        {
                            await handlerTask.ConfigureAwait(false);
                            sub.ResetSlowStrikes();
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // The handler hit the budget cancellation from INSIDE
                            // its own body — an over-budget strike, not a death.
                            await RecordSlowStrikeAsync(sub, Task.CompletedTask).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Subscriber threw exception — removing dead subscriber");
                        MarkDead(sub);
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
        finally
        {
            TrackDequeued(enqueuedTicks);
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
    ///     Total slow-path publishes since construction. The zero-subscriber /
    ///     zero-scrollback / zero-middleware fast path returns before this
    ///     counter, so it counts exactly the publishes that entered the queue.
    /// </summary>
    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <summary>
    ///     Publishes currently inside <see cref="PublishAsync" /> (submitted
    ///     but not yet fanned out). Zero in the quiescent state.
    /// </summary>
    public long InflightPublishCount => Volatile.Read(ref _inflightPublishCount);

    /// <summary>
    ///     Age of the oldest still-pending publish (#47 queue-age gauge).
    ///     <see cref="TimeSpan.Zero" /> when nothing is in flight. Exact in the
    ///     single-publisher case; under concurrency the baseline re-anchors to
    ///     "now" when the tracked oldest leaves while others remain, so the
    ///     gauge under-reports rather than over-reports.
    /// </summary>
    public TimeSpan OldestPendingAge
    {
        get
        {
            if (Volatile.Read(ref _inflightPublishCount) == 0)
            {
                return TimeSpan.Zero;
            }

            long oldest = Volatile.Read(ref _oldestEnqueuedTicks);
            if (oldest == 0)
            {
                return TimeSpan.Zero;
            }

            return StopwatchTicksToTimeSpan(Stopwatch.GetTimestamp() - oldest);
        }
    }

    /// <summary>
    ///     Slowest slow-path publish observed since construction
    ///     (submission → fan-out complete). Monotonic max; use the telemetry
    ///     layer's histograms for percentiles.
    /// </summary>
    public TimeSpan MaxDispatchDuration => StopwatchTicksToTimeSpan(Volatile.Read(ref _maxDispatchTicks));

    /// <summary>
    ///     Record a submission timestamp as the oldest pending unless an older
    ///     baseline is already tracked. Lock-free CAS-min loop.
    /// </summary>
    private void TrackEnqueued(long enqueuedTicks)
    {
        long observed = Volatile.Read(ref _oldestEnqueuedTicks);
        while (observed == 0 || enqueuedTicks < observed)
        {
            if (Interlocked.CompareExchange(ref _oldestEnqueuedTicks, enqueuedTicks, observed) == observed)
            {
                break;
            }

            observed = Volatile.Read(ref _oldestEnqueuedTicks);
        }
    }

    /// <summary>
    ///     Leave the in-flight set: fold this publish's dispatch duration into
    ///     the max and reset (or re-anchor) the oldest-pending baseline.
    ///     Never throws — safe inside finally.
    /// </summary>
    private void TrackDequeued(long enqueuedTicks)
    {
        long elapsed = Stopwatch.GetTimestamp() - enqueuedTicks;
        long observedMax = Volatile.Read(ref _maxDispatchTicks);
        while (elapsed > observedMax)
        {
            if (Interlocked.CompareExchange(ref _maxDispatchTicks, elapsed, observedMax) == observedMax)
            {
                break;
            }

            observedMax = Volatile.Read(ref _maxDispatchTicks);
        }

        if (Interlocked.Decrement(ref _inflightPublishCount) == 0)
        {
            // Last publisher out — nothing pending, age resets to zero.
            Volatile.Write(ref _oldestEnqueuedTicks, 0);
        }
        else if (enqueuedTicks <= Volatile.Read(ref _oldestEnqueuedTicks))
        {
            // The tracked oldest left while others remain; their individual
            // starts are not retained, so re-anchor to now (see
            // OldestPendingAge docs: under-report, never over-report).
            Volatile.Write(ref _oldestEnqueuedTicks, Stopwatch.GetTimestamp());
        }
    }

    private static TimeSpan StopwatchTicksToTimeSpan(long stopwatchTicks)
    {
        if (stopwatchTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        if (stopwatchTicks > long.MaxValue / TimeSpan.TicksPerSecond)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks(stopwatchTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }

    /// <summary>
    ///     Internal subscriber record. Sequential layout for cache-friendly iteration.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private sealed class Subscription
    {
        public Func<AgentEvent, CancellationToken, ValueTask> Handler { get; }

        private int _slowStrikes;

        public Subscription(Func<AgentEvent, CancellationToken, ValueTask> handler)
        {
            Handler = handler;
        }

        public int Strike() => Interlocked.Increment(ref _slowStrikes);

        public void ResetSlowStrikes() => Interlocked.Exchange(ref _slowStrikes, 0);
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
