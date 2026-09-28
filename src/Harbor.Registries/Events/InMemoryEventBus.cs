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
///                 <see cref="PublishAsync" /> fast path: when scrollback is
///                 disabled (<c>maxScrollback &lt;= 0</c>), no sink registered
///                 <see cref="EventBusSinkKind.Mandatory" />, and there are
///                 zero subscribers, the method returns before touching any
///                 collection — zero allocation, synchronous completion. The
///                 mandatory/optional verdict is declared by each sink and
///                 computed once in the constructor
///                 (<see cref="HasMandatorySink" />,
///                 <see cref="FastPathEligible" />), never sniffed per publish;
///                 the enumeration behind it is
///                 <c>docs/EVENT_BUS_SINKS.md</c> (#47/S3). Optional sinks that
///                 are attached on this path are still drained, and both the
///                 drain and any drop are counted
///                 (<see cref="OptionalSinkDrainCount" />,
///                 <see cref="OptionalSinkDropCount" />). Otherwise: lock-free
///                 snapshot read of subscriptions, one in-place slot write under
///                 a short lock for scrollback, and a pooled buffer for
///                 dead-subscriber collection.
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
///                 telemetry layer, which polls these counters through
///                 <see cref="IEventBusQueueMetrics" />.
///             </item>
///             <item>
///                 Dispatch-duration distribution (#47/S2): the same slow-path
///                 envelope also folds each completed publish into a
///                 fixed-capacity ring of
///                 <see cref="DispatchSampleWindowCapacity" /> monotonic ticks
///                 (allocated once, 2 KB, never grows). It backs
///                 <see cref="DispatchDurationPercentile" /> — p50/p95/p99 answer
///                 "is everything late, or was that one outlier?", which the
///                 monotonic max alone cannot. One extra short lock per
///                 completed publish; no allocation, no await.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class InMemoryEventBus : IEventBus, IEventBusQueueMetrics
{
    /// <summary>Default per-handler budget (A4): one slow subscriber may hold
    /// the fan-out for at most this long before it is left behind.</summary>
    public static readonly TimeSpan DefaultHandlerBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Retained dispatch-duration samples per bus (#47/S2). The percentile
    ///     window is a fixed-capacity ring allocated once in the constructor:
    ///     retained memory is O(1) (2 KB) no matter how many publishes run, and
    ///     the window is a power of two so the slot index is a mask, not a
    ///     modulo. 256 samples keep p99 above p50 for any realistic burst while
    ///     staying inside the poll budget of the telemetry layer.
    /// </summary>
    public const int DispatchSampleWindowCapacity = 256;

    /// <summary>Mask for the ring index (<see cref="DispatchSampleWindowCapacity" /> is a power of two).</summary>
    private const int DispatchSampleMask = DispatchSampleWindowCapacity - 1;

    /// <summary>Consecutive over-budget dispatches before a subscriber is evicted.</summary>
    private const int MaxSlowStrikes = 3;

    /// <summary>Per-dispatch budget; TimeSpan.Zero disables the budget entirely.</summary>
    private readonly TimeSpan _handlerBudget;

    /// <summary>
    ///     Single-slot cache of spare budget <see cref="CancellationTokenSource" />
    ///     instances (#249). The 1/N-subscriber publish path used to allocate a
    ///     fresh CTS per publish even when every handler completed synchronously;
    ///     publishes now rent this instance (allocating one on first use or
    ///     contention) and return it through <c>TryReset</c>-gated caching. The
    ///     cached instance is always unlinked — linkage to the publisher's token
    ///     is emulated per publish with a registration (see
    ///     <see cref="DispatchToSubscribersAsync" />) — and, since #391, it also
    ///     never carries a pending budget timer: the timer is armed only for
    ///     handlers that outlive their synchronous part (see
    ///     <see cref="DispatchToOneAsync" />), which is what makes the instance
    ///     genuinely reusable instead of being discarded on every publish.
    ///     Exchanged with <c>Interlocked</c> only: contention losers dispose
    ///     their spare instead of blocking.
    /// </summary>
    private CancellationTokenSource? _pooledBudgetCts;

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
    ///     Whether at least one registered sink declared
    ///     <see cref="EventBusSinkKind.Mandatory" /> (#47/S3). Decided once, here,
    ///     from the sinks' own verdicts — never re-derived per publish, because a
    ///     per-publish sniff would be exactly the "bypass blindly" the slice
    ///     forbids. Defaults to mandatory for any sink that does not declare a
    ///     verdict, so an unconsidered sink always keeps the full path.
    /// </summary>
    private readonly bool _hasMandatorySink;

    /// <summary>
    ///     Whether this bus is <em>able</em> to take the zero-subscriber fast
    ///     path: scrollback disabled and no mandatory sink (#47/S3). A
    ///     composition-time constant, published as <see cref="FastPathEligible" />
    ///     so the decision is observable from outside the class instead of being
    ///     a claim buried in a boolean expression.
    /// </summary>
    private readonly bool _fastPathEligible;

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
    ///     the telemetry layer, which polls these counters through
    ///     <see cref="IEventBusQueueMetrics" />.
    /// </summary>
    private long _publishedCount;
    private long _inflightPublishCount;
    private long _oldestEnqueuedTicks;
    private long _maxDispatchTicks;

    /// <summary>
    ///     Publishes that took the fast path — nobody to notify, nothing to
    ///     retain, no mandatory sink (#47/S3). The queue-age envelope is skipped
    ///     there by design, so this counter is what keeps the skip visible:
    ///     <c>FastPathCount + PublishedCount</c> is the total publish count, and
    ///     the ratio is the fraction of publishes that qualified.
    /// </summary>
    private long _fastPathCount;

    /// <summary>
    ///     Fast-path publishes that had optional sinks attached and therefore
    ///     ran the optional pipeline instead of short-circuiting (#47/S3).
    ///     Proves the optional sinks were drained, not skipped.
    /// </summary>
    private long _optionalSinkDrainCount;

    /// <summary>
    ///     Optional sinks that dropped an event on the fast path (#47/S3).
    ///     A drop is always counted and logged — the fast path is allowed to
    ///     skip work, never to lose an event quietly.
    /// </summary>
    private long _optionalSinkDropCount;

    /// <summary>
    ///     Bounded dispatch-duration window (#47/S2). A fixed-capacity ring of
    ///     monotonic stopwatch ticks, allocated once — the percentile view the
    ///     telemetry layer polls. Unlike the counters above it is guarded by a
    ///     short lock: the write is a single slot store, and the read is a
    ///     copy-then-sort that must not observe a half-updated window. O(1)
    ///     retained samples, zero allocation per publish.
    /// </summary>
    private readonly long[] _dispatchWindowTicks = new long[DispatchSampleWindowCapacity];

    /// <summary>Guards <see cref="_dispatchWindowTicks" />, <see cref="_dispatchWindowHead" />
    /// and <see cref="_dispatchWindowCount" />.</summary>
    private readonly object _dispatchWindowLock = new();

    /// <summary>Next slot to write in <see cref="_dispatchWindowTicks" /> (wraps at the capacity).</summary>
    private int _dispatchWindowHead;

    /// <summary>Valid entries in the window, saturating at <see cref="DispatchSampleWindowCapacity" />.</summary>
    private int _dispatchWindowCount;

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

        // #47/S3: the mandatory set is computed ONCE, from each sink's own
        // declared verdict, right here at composition time. Nothing on the
        // publish path re-derives it, and no sink type is special-cased by
        // name — a host that registers a mandatory sink keeps the full path
        // without anyone editing this class.
        IReadOnlyList<IEventBusMiddleware> sinks = _middlewares;
        for (int i = 0; i < sinks.Count; i++)
        {
            if (sinks[i].SinkKind == EventBusSinkKind.Mandatory)
            {
                _hasMandatorySink = true;
                break;
            }
        }

        _fastPathEligible = _maxScrollback == 0 && !_hasMandatorySink;
    }

    /// <summary>
    ///     Whether a registered sink forces this bus off the fast path
    ///     (#47/S3). True for any middleware that declared
    ///     <see cref="EventBusSinkKind.Mandatory" />, and for every sink that
    ///     did not declare a verdict at all (the interface default is
    ///     mandatory). A composition-time constant.
    /// </summary>
    public bool HasMandatorySink => _hasMandatorySink;

    /// <summary>
    ///     Whether this bus is able to take the zero-subscriber fast path:
    ///     scrollback disabled and no mandatory sink (#47/S3). A composition-time
    ///     constant, not a per-publish guess — <see cref="FastPathCount" />
    ///     divided by <c>FastPathCount + PublishedCount</c> is the measured
    ///     fraction of publishes that actually qualified on this bus.
    /// </summary>
    public bool FastPathEligible => _fastPathEligible;

    /// <inheritdoc />
    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // ── Fast path (#47/S3). Provable, not hopeful: the two terms below are
        //    the complete list of reasons a publish can go unobserved, and each
        //    one is either a composition-time constant or a lock-free snapshot
        //    read. The verdict for every registered sink is enumerated in
        //    docs/EVENT_BUS_SINKS.md — the guard was not tightened before that
        //    table existed.
        if (_fastPathEligible)
        {
            // Volatile-free read of the snapshot: ImmutableArray<T> is
            // reference-sized, and the existing slow path reads the same field
            // the same way (no torn reads are possible for a single reference).
            var snapshot = _subscriptions;
            if (snapshot.IsEmpty)
            {
                // Nothing can observe this publish: no subscriber, nothing to
                // retain, no mandatory sink. The queue-age envelope is skipped
                // and the skip is counted, so the total publish count stays
                // exact (FastPathCount + PublishedCount) instead of quietly
                // under-reporting.
                //
                // NOTE (#97 per-delta): the Debug log in PublishSlowAsync must
                // stay AFTER this check. LogDebug evaluates
                // @event.GetType().Name + the params object[] eagerly even when
                // Debug is off, which allocated on EVERY publish (one per
                // streaming delta) and broke the zero-alloc claim.
                Interlocked.Increment(ref _fastPathCount);

                var sinks = _middlewares;
                if (sinks.Count == 0)
                {
                    return Task.CompletedTask;
                }

                // Reachable only with OPTIONAL sinks attached (a mandatory one
                // would have made this bus ineligible). They are drained, not
                // skipped: "optional" is a statement about what breaks, not a
                // licence for silence. Synchronous sinks — the overwhelming
                // majority — keep the whole call allocation-free.
                Interlocked.Increment(ref _optionalSinkDrainCount);
                return DrainOptionalSinksAsync(@event, sinks, ct);
            }
        }

        return PublishSlowAsync(@event, ct);
    }

    /// <summary>
    ///     Run the optional-sink pipeline on the fast path (#47/S3). No
    ///     subscriber, no scrollback and no mandatory sink can observe the
    ///     publish, so the optional sinks are the only consumers left and they
    ///     still run — losing the event to a sampler costs observability, but
    ///     the bus does not get to make that choice on their behalf. An
    ///     explicit <c>false</c> (or a throw) is counted in
    ///     <see cref="OptionalSinkDropCount" />, so even a drop is visible.
    /// </summary>
    private async Task DrainOptionalSinksAsync(
        AgentEvent @event,
        IReadOnlyList<IEventBusMiddleware> sinks,
        CancellationToken ct)
    {
        for (int i = 0; i < sinks.Count; i++)
        {
            IEventBusMiddleware sink = sinks[i];
            try
            {
                bool continuePipeline = await sink.ProcessAsync(ref @event, ct).ConfigureAwait(false);
                if (!continuePipeline)
                {
                    Interlocked.Increment(ref _optionalSinkDropCount);

                    // IsEnabled guard (#47): the drop path is cold, but the
                    // params array + GetType().Name evaluate eagerly even when
                    // Trace is off.
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Event {EventType} dropped by optional sink {Middleware}",
                            @event.GetType().Name, sink.Name);
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _optionalSinkDropCount);
                _logger.LogWarning(ex, "Middleware {Middleware} threw — event dropped", sink.Name);
                return;
            }
        }
    }

    /// <summary>
    ///     The full publish path: queue-age envelope, mandatory + optional sink
    ///     pipeline, scrollback append, and awaited fan-out. Reached whenever
    ///     there is a subscriber, something to retain, or a mandatory sink
    ///     (#47/S3).
    /// </summary>
    private async Task PublishSlowAsync(AgentEvent @event, CancellationToken ct)
    {
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
            var (continued, current) = await RunMiddlewareAsync(@event, ct).ConfigureAwait(false);
            if (!continued)
            {
                return;
            }

            @event = current;

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
            await DispatchToSubscribersAsync(snapshot, @event, ct).ConfigureAwait(false);
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
    ///     Total slow-path publishes since construction — the publishes that
    ///     entered the queue-age envelope. The fast path (#47/S3) returns before
    ///     this counter, so the total publish count is
    ///     <c>FastPathCount + PublishedCount</c> and never under-reports.
    /// </summary>
    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <summary>
    ///     Publishes that took the fast path: no subscriber, no scrollback, no
    ///     mandatory sink (#47/S3). Paired with <see cref="PublishedCount" /> it
    ///     yields the measured qualification fraction
    ///     (<c>FastPathCount / (FastPathCount + PublishedCount)</c>) — the
    ///     number #47/S3 asks for instead of an estimate.
    /// </summary>
    public long FastPathCount => Interlocked.Read(ref _fastPathCount);

    /// <summary>
    ///     Fast-path publishes that had optional sinks attached and ran them
    ///     instead of short-circuiting (#47/S3). Proof that the optional sinks
    ///     are drained, not skipped.
    /// </summary>
    public long OptionalSinkDrainCount => Interlocked.Read(ref _optionalSinkDrainCount);

    /// <summary>
    ///     Optional sinks that dropped an event on the fast path — an explicit
    ///     <c>false</c> or a throw (#47/S3). Silent loss is not an option, so
    ///     even this is counted; the path logs the drop too.
    /// </summary>
    public long OptionalSinkDropCount => Interlocked.Read(ref _optionalSinkDropCount);

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
    ///     (submission → fan-out complete). Monotonic max; use
    ///     <see cref="DispatchDurationPercentile" /> for the distribution view of
    ///     the same quantity — the max alone cannot tell one 900 ms outlier from
    ///     a bus where everything is 900 ms late.
    /// </summary>
    public TimeSpan MaxDispatchDuration => StopwatchTicksToTimeSpan(Volatile.Read(ref _maxDispatchTicks));

    /// <inheritdoc />
    public int DispatchSampleCapacity => DispatchSampleWindowCapacity;

    /// <inheritdoc />
    public int DispatchSampleCount
    {
        get
        {
            lock (_dispatchWindowLock)
            {
                return _dispatchWindowCount;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan DispatchDurationPercentile(double quantile)
    {
        if (double.IsNaN(quantile) || quantile <= 0)
        {
            quantile = 0;
        }
        else if (quantile > 1)
        {
            quantile = 1;
        }

        // Pooled: the telemetry layer polls, the publish path never touches it.
        long[] window = ArrayPool<long>.Shared.Rent(DispatchSampleWindowCapacity);
        try
        {
            int count;
            lock (_dispatchWindowLock)
            {
                count = _dispatchWindowCount;
                if (count == 0)
                {
                    return TimeSpan.Zero;
                }

                // Copy under the lock; the order inside the window is irrelevant
                // to a percentile, only the multiset matters.
                Array.Copy(_dispatchWindowTicks, window, count);
            }

            Array.Sort(window, 0, count);

            // Nearest-rank: index = ceil(q * n) - 1, clamped into the window.
            int index = (int)Math.Ceiling(quantile * count) - 1;
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= count)
            {
                index = count - 1;
            }

            return StopwatchTicksToTimeSpan(window[index]);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(window);
        }
    }

    /// <summary>
    ///     Fold one completed publish into the bounded window (#47/S2). Called
    ///     from the same slow-path envelope as the max, so the fast path never
    ///     enters the histogram. One slot store under a short lock — no
    ///     allocation, no await, so it is safe inside <c>finally</c>.
    /// </summary>
    private void RecordDispatchSample(long elapsedTicks)
    {
        lock (_dispatchWindowLock)
        {
            _dispatchWindowTicks[_dispatchWindowHead] = elapsedTicks;
            _dispatchWindowHead = (_dispatchWindowHead + 1) & DispatchSampleMask;
            if (_dispatchWindowCount < DispatchSampleWindowCapacity)
            {
                _dispatchWindowCount++;
            }
        }
    }

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
    ///     the max and the bounded percentile window (#47/S2), and reset (or
    ///     re-anchor) the oldest-pending baseline. Never throws — safe inside
    ///     finally.
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

        RecordDispatchSample(elapsed);

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
    ///     Run the middleware pipeline. Returns whether the event continues
    ///     to scrollback + fan-out, plus the (possibly replaced) event.
    ///     Dropped events never reach scrollback or subscribers.
    /// </summary>
    private async Task<(bool Continue, AgentEvent Event)> RunMiddlewareAsync(AgentEvent @event, CancellationToken ct)
    {
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

                        return (false, @event);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Middleware {Middleware} threw — event dropped", mw.Name);
                    return (false, @event);
                }
            }
        }

        return (true, @event);
    }

    /// <summary>How one subscriber dispatch resolved.</summary>
    private enum DispatchOutcome : byte
    {
        Completed,
        Slow,
    }

    /// <summary>
    ///     Fan-out to subscribers under the A4 per-handler budget. Dead
    ///     subscribers are collected into a pooled buffer and evicted after
    ///     the loop.
    /// </summary>
    private async Task DispatchToSubscribersAsync(
        ImmutableArray<Subscription> snapshot, AgentEvent @event, CancellationToken ct)
    {
        Subscription[]? dead = null;
        int deadCount = 0;
        CancellationTokenSource? budgetCts = null;
        CancellationTokenRegistration budgetLink = default;
        try
        {
            int snapshotLength = snapshot.Length;
            bool budgetEnabled = _handlerBudget > TimeSpan.Zero;
            if (budgetEnabled)
            {
                // #249: rent the per-handler budget CTS from the single-slot
                // pool instead of allocating one per publish. The cached
                // instance is always unlinked; when the outer token can fire,
                // the link is emulated with a registration that cancels the
                // rented source (torn down in the finally below — the same
                // lifetime the per-publish linked CTS had under `using`).
                // No linked registration when the outer token can never fire:
                // the rented CTS behaves identically (the per-dispatch budget
                // still applies) and skips the linked-cancellation allocation
                // (#47).
                budgetCts = RentBudgetCts();
                if (ct.CanBeCanceled)
                {
                    budgetLink = ct.Register(
                        static state => ((CancellationTokenSource)state!).Cancel(), budgetCts);
                }
            }

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
                await ObserveOrphanAsync(handlerTask).ConfigureAwait(false);
            }

            for (int i = 0; i < snapshotLength; i++)
            {
                var sub = snapshot[i];
                try
                {
                    var (outcome, orphan) = await DispatchToOneAsync(sub, @event, ct, budgetCts, budgetEnabled)
                        .ConfigureAwait(false);
                    if (outcome == DispatchOutcome.Slow)
                    {
                        await RecordSlowStrikeAsync(sub, orphan!).ConfigureAwait(false);
                    }
                    else
                    {
                        sub.ResetSlowStrikes();
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
            // Unlink before pool-return: a live registration would make
            // TryReset refuse the instance and silently drain the pool.
            budgetLink.Dispose();
            if (budgetCts is not null)
            {
                ReturnBudgetCts(budgetCts);
            }

            if (dead is not null)
            {
                // Clear references so the pooled array doesn't keep the Subscription
                // (and indirectly the handler delegate) alive after return.
                Array.Clear(dead, 0, deadCount);
                ArrayPool<Subscription>.Shared.Return(dead);
            }
        }
    }

    /// <summary>
    ///     Rent a spare budget <see cref="CancellationTokenSource" /> (#249).
    ///     Returns the cached instance when a previous publish left one behind,
    ///     otherwise allocates. The rented instance is always unlinked, reset
    ///     and free of a pending budget timer — ready for
    ///     <c>CancelAfter</c> if a handler needs the budget, and untouched if
    ///     not, which is what lets the next publish rent it again.
    /// </summary>
    private CancellationTokenSource RentBudgetCts() =>
        Interlocked.Exchange(ref _pooledBudgetCts, null) ?? new CancellationTokenSource();

    /// <summary>
    ///     Return a rented budget <see cref="CancellationTokenSource" /> to the
    ///     single-slot cache (#249). <c>TryReset</c> refuses a source that was
    ///     cancelled (a non-cooperative orphan still holding the token, or the
    ///     emulated link firing) or that still carries callback registrations
    ///     — those are disposed instead of poisoning the pool. On contention
    ///     the spare is likewise disposed rather than retained.
    /// </summary>
    private void ReturnBudgetCts(CancellationTokenSource cts)
    {
        if (cts.TryReset())
        {
            Interlocked.Exchange(ref _pooledBudgetCts, cts)?.Dispose();
        }
        else
        {
            cts.Dispose();
        }
    }

    /// <summary>
    ///     Dispatch one event to one subscriber under the budget. Returns the
    ///     outcome plus the orphaned handler task when over budget (to observe).
    /// </summary>
    private async Task<(DispatchOutcome Outcome, Task? Orphan)> DispatchToOneAsync(
        Subscription sub,
        AgentEvent @event,
        CancellationToken ct,
        CancellationTokenSource? budgetCts,
        bool budgetEnabled)
    {
        if (!budgetEnabled)
        {
            await sub.Handler(@event, ct).ConfigureAwait(false);
            return (DispatchOutcome.Completed, null);
        }

        var budgetSource = budgetCts!;
        ValueTask dispatch = sub.Handler(@event, budgetSource.Token);
        if (dispatch.IsCompletedSuccessfully)
        {
            // #391 follow-up: the budget timer is deliberately NOT armed here.
            // A handler that already finished needs no cancellation, and arming
            // it anyway is what stopped the pooled source from recycling — the
            // pending timer is what makes TryReset refuse the instance (so the
            // next publish allocates a fresh one) and it costs a timer arm +
            // disarm per subscriber besides. A pristine source is what lets
            // ReturnBudgetCts hand it to the next publish.
            return (DispatchOutcome.Completed, null);
        }

        Task handlerTask = dispatch.AsTask();
        if (!handlerTask.IsCompleted)
        {
            // The dispatch outlived its synchronous part, so the budget applies
            // from here: arming after the handler started is equivalent for
            // every observable purpose (a token the emulated link already
            // cancelled makes CancelAfter a no-op either way, and the window
            // shifts only by the handler's synchronous prologue). A dispatch
            // that already completed — fault or cancellation, both resolved by
            // the branches below — needs no timer either, and arming it there
            // would only cost the pool its instance.
            budgetSource.CancelAfter(_handlerBudget);
        }

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
                return (DispatchOutcome.Completed, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The handler hit the budget cancellation from INSIDE
                // its own body — an over-budget strike, not a death.
                return (DispatchOutcome.Slow, Task.CompletedTask);
            }
        }

        Task winner = await Task.WhenAny(handlerTask, Task.Delay(Timeout.InfiniteTimeSpan, ct))
            .ConfigureAwait(false);
        if (winner != handlerTask)
        {
            // Publisher slice elapsed while the handler still runs:
            // leave it behind (observed), count the strike.
            return (DispatchOutcome.Slow, handlerTask);
        }

        try
        {
            await handlerTask.ConfigureAwait(false);
            return (DispatchOutcome.Completed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The handler hit the budget cancellation from INSIDE
            // its own body — an over-budget strike, not a death.
            return (DispatchOutcome.Slow, Task.CompletedTask);
        }
    }

    /// <summary>Keep an orphaned slow-subscriber handler observed so late faults are never lost.</summary>
    private async Task ObserveOrphanAsync(Task handlerTask)
    {
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
