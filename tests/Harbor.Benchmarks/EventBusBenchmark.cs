using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;

namespace Harbor.Benchmarks;

/// <summary>
///     Publish-path measurement for <see cref="InMemoryEventBus.PublishAsync" />
///     (#391 — closes the CI-bench re-measurement follow-up left open by #152).
///     Retention (scrollback on/off) × drain (0 / 1 / N subscribers), one row per
///     point. The delivery-contract split required by #408 lives in
///     <see cref="EventBusDeliveryBenchmark" />, further down this same file.
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <see cref="InMemoryEventBus.PublishAsync" /> of a
///         single <see cref="TurnStartEvent" />, against a bus whose scrollback capacity
///         and subscriber count the row name states. Nothing else is inside the
///         measured region — no scrollback tail read, no telemetry, no setup.</item>
///         <item><c>Payload:</c> 1 event — no session/message ids, no text body, no tool
///         args. Built once in <c>Setup</c> and republished by every iteration, so the
///         allocation column is attributable to the bus alone. (The sibling
///         <c>EventBusScrollbackBenchmark</c> still mints a fresh event per iteration.)</item>
///         <item><c>StateReset:</c> none, by construction — and deliberately so. The only
///         state <c>PublishAsync</c> mutates is (a) the scrollback ring, a fixed-capacity
///         overwrite that is idempotent here because the same immutable
///         <see cref="AgentEvent" /> instance is republished, and (b) the #47 queue-age
///         counters, which are monotonic <c>long</c>s or which return to quiescent
///         because every measured publish is fully drained before the next iteration
///         starts. Nothing accumulates, so an <c>[IterationCleanup]</c> would be dead code.</item>
///         <item><c>Drain:</c> per row, and never fire-and-forget. The <c>*_ScrollbackOn</c>
///         drain rows (<see cref="PublishAsync_1Sub" />, <see cref="PublishAsync_1Sub_AsyncDrain" />)
///         complete only after the handler has returned;
///         <see cref="EventBusBenchmarkFanout.PublishAsync_NSub" /> only after all
///         <c>SubscriberCount</c> handlers have. <see cref="PublishAsync_0Sub_ScrollbackOn" />
///         has no consumer at all, so there is nothing to drain.</item>
///         <item><c>RetainedState:</c> the scrollback ring on the three retention-on buses,
///         pre-filled to <c>ScrollbackCapacity</c> in <c>Setup</c> so measured iterations take
///         the steady in-place overwrite branch
///         (<c>InMemoryEventBus.cs:380</c>) rather than the "ring still filling" one.
///         <c>InMemoryEventBus</c> exposes no drain, so the ring stays warm by design —
///         its warm-vs-cold state is part of the cost under measurement and is not
///         reset between iterations.</item>
///         <item><c>AwaitSemantics:</c> every row returns the <c>Task</c> the bus produced —
///         the measured region is the publish call itself, and BenchmarkDotNet's async
///         harness consumes the task before the next iteration. Whether the awaited work
///         completed synchronously or involved a real suspension is stated per row,
///         because the two cost different things: all rows but
///         <see cref="PublishAsync_1Sub_AsyncDrain" /> complete synchronously, and that
///         one suspends exactly once (via the row's own <c>Task.Yield()</c> handler)
///         and is still fully awaited before <c>PublishAsync</c> returns.</item>
///         <item><c>AllocAttribution:</c> stated per row against its measured B/op —
///         see <c>docs/BENCHMARKS.md</c> §5.4. The class-wide rule: buses are built with
///         the production defaults (<c>NullLogger</c>,
///         <see cref="InMemoryEventBus.DefaultHandlerBudget" /> = 250 ms), so every drain
///         row also rents the #249 single-slot pooled budget CTS and schedules a
///         <c>CancelAfter</c> on it.</item>
///     </list>
///     <para>
///         <b>Why this file was rewritten.</b> The pre-#391 version had a single
///         <c>PublishAsync</c> method over <c>[Params(0, 1, 10, 100)]</c> on one bus
///         built as <c>new InMemoryEventBus(maxScrollback: 1024)</c>. That shape
///         <i>structurally cannot</i> measure the fast path: the zero-allocation early
///         return in <see cref="InMemoryEventBus.PublishAsync" />
///         (src/Harbor.Registries/Events/InMemoryEventBus.cs:224) requires
///         <c>_maxScrollback == 0 &amp;&amp; _middlewares.Count == 0 &amp;&amp; _subscriptions.IsEmpty</c>,
///         so with scrollback enabled every "0 subscriber" case fell through to the
///         slow path. "The fast path works" was untested by construction, and the
///         "8.1 KB @0 subscribers" figure in docs/BENCHMARKS.md is the 2026-08-22
///         pre-ring (<c>ImmutableArray</c> scrollback copy) measurement that has never
///         been re-taken since 2f9debf.
///     </para>
///     <para>
///         <b>The two axes.</b> Every case is one point on (retention × drain):
///         <list type="table">
///             <item><term>retention off</term><description>scrollback capacity 0 — the ring append is skipped and the fast path becomes reachable.</description></item>
///             <item><term>retention on</term><description>scrollback capacity 1024, pre-filled to capacity — the cost the historical rows measured.</description></item>
///             <item><term>drain off</term><description>no subscribers — enqueue-only.</description></item>
///             <item><term>drain on</term><description>1 / N subscribers, <b>drained inside the measured region</b> (see <c>Await semantics</c>).</description></item>
///         </list>
///         The <c>*_ScrollbackOff</c> drain cases therefore isolate the drain from
///         the enqueue, and the scrollback-on drain cases are the enqueue+drain pair
///         the old single-method benchmark could not separate.
///     </para>
///     <para>
///         <b>Per-case contract (#46/#391).</b> The seven fields above are the
///         class-wide default; each row below then states the deltas that make it
///         attributable — what its <c>Operation:</c> is, whether its
///         <c>AwaitSemantics:</c> suspend, and what its <c>AllocAttribution:</c>
///         measured. These rows exist to give #47/S3 and #47/S4 a real baseline; the
///         merge-gated side is the <c>#186</c> allocation tripwires in
///         <c>tests/Harbor.Registries.Tests/AllocationBudgetTests.cs</c> plus the
///         reachability tests in <c>EventBusFastPathTests.cs</c>.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusBenchmark
{
    /// <summary>
    ///     Scrollback capacity for the retention-on buses. Kept at the historical
    ///     1024 so the enqueue-side rows remain comparable with the 2026-08-22
    ///     numbers in docs/BENCHMARKS.md.
    /// </summary>
    private const int ScrollbackCapacity = 1024;

    /// <summary>Scrollback on, no subscribers — the enqueue-only case.</summary>
    private InMemoryEventBus _enqueueOnlyBus = null!;

    /// <summary>Scrollback OFF, no subscribers — the fast path.</summary>
    private InMemoryEventBus _fastPathBus = null!;

    /// <summary>Scrollback on, one synchronous subscriber — enqueue + drain.</summary>
    private InMemoryEventBus _enqueueAndDrainBus = null!;

    /// <summary>Scrollback OFF, one synchronous subscriber — drain without enqueue.</summary>
    private InMemoryEventBus _drainOnlyBus = null!;

    /// <summary>Scrollback on, one genuinely asynchronous subscriber.</summary>
    private InMemoryEventBus _asyncDrainBus = null!;

    /// <summary>Built once in <see cref="Setup" />; see the payload note on the class.</summary>
    private AgentEvent _event = null!;

    /// <summary>Warm the rings, then attach consumers. Ordering matters: priming must
    /// not dispatch, so subscriptions are added only after the rings are full.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _event = new TurnStartEvent(42);

        _fastPathBus = new InMemoryEventBus(maxScrollback: 0);
        _drainOnlyBus = new InMemoryEventBus(maxScrollback: 0);
        _enqueueOnlyBus = new InMemoryEventBus(maxScrollback: ScrollbackCapacity);
        _enqueueAndDrainBus = new InMemoryEventBus(maxScrollback: ScrollbackCapacity);
        _asyncDrainBus = new InMemoryEventBus(maxScrollback: ScrollbackCapacity);

        // No consumers attached yet → priming exercises the same 0-subscriber
        // append-only path the enqueue-only case measures, and cannot suspend.
        PrimeRing(_enqueueOnlyBus);
        PrimeRing(_enqueueAndDrainBus);
        PrimeRing(_asyncDrainBus);

        _enqueueAndDrainBus.Subscribe(SynchronousConsumer);
        _drainOnlyBus.Subscribe(SynchronousConsumer);
        _asyncDrainBus.Subscribe(AsynchronousConsumer);
    }

    /// <summary>
    ///     Op: one <see cref="InMemoryEventBus.PublishAsync" /> on a bus with no
    ///     scrollback, no middleware and no subscribers. This is the
    ///     <c>_maxScrollback == 0 &amp;&amp; _middlewares.Count == 0 &amp;&amp;
    ///     _subscriptions.IsEmpty</c> early return
    ///     (src/Harbor.Registries/Events/InMemoryEventBus.cs:224) — the only
    ///     configuration in which the fast-path claim is testable at all.
    ///     <para>
    ///         Await semantics: completes synchronously; the returned task is the
    ///         async builder's cached completed task.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: must be <b>0 B</b>. Everything the early return
    ///         skips is attributed elsewhere: the queue-age envelope
    ///         (<c>Stopwatch.GetTimestamp</c> + 3 <c>Interlocked</c> ops, no
    ///         allocation), <c>RunMiddlewareAsync</c>'s
    ///         <c>Task&lt;ValueTuple&lt;bool, AgentEvent&gt;&gt;</c>
    ///         (InMemoryEventBus.cs:521) and the ring append are all strictly below
    ///         the return, which is the whole point of the case. The #186 tripwire
    ///         <c>PublishAsync_ZeroSubscribers_IsAllocationFree</c> pins the same
    ///         claim merge-gated.
    ///     </para>
    ///     <para>
    ///         Measured 2026-09-28 in CI (run 36452709074, AMD EPYC 7763, BDN 0.15.8):
    ///         <b>8.15 ns, 0 B</b> — MemoryDiagnoser printed <c>-</c> for both <c>Gen0</c>
    ///         and <c>Allocated</c>, i.e. not one Gen0 collection during the iteration.
    ///         See docs/BENCHMARKS.md §5.3.2.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_0Sub_ScrollbackOff", Baseline = true)]
    public Task PublishAsync_0Sub_ScrollbackOff() => _fastPathBus.PublishAsync(_event);

    /// <summary>
    ///     Op: one <see cref="InMemoryEventBus.PublishAsync" /> with scrollback on
    ///     and no subscribers — <b>enqueue only, nothing drains it</b>. Same bus
    ///     shape as the stale "8.1 KB @0 subscribers" row, so this is its
    ///     re-measurement; the difference between the two rows is the fan-out the
    ///     fast path avoids.
    ///     <para>
    ///         Await semantics: the middleware pipeline is awaited but completes
    ///         synchronously (no middleware registered), so the task is already
    ///         complete on return.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: <b>non-zero</b> and to grow if this case is ever
    ///         changed to await a real suspension. The resident allocation is the
    ///         <c>Task&lt;ValueTuple&lt;bool, AgentEvent&gt;&gt;</c> produced by
    ///         <c>RunMiddlewareAsync</c> (InMemoryEventBus.cs:521, result set at :553) —
    ///         <c>Task.FromResult</c> does not cache non-primitive result types, so one
    ///         task escapes per publish. The pre-ring scrollback copy (1024 refs × 8 B ≈
    ///         8 KB) is gone with 2f9debf: <c>AppendScrollback</c> now overwrites a fixed
    ///         slot (InMemoryEventBus.cs:380) and allocates nothing.
    ///     </para>
    ///     <para>
    ///         Measured 2026-09-28 in CI (run 36452709074): <b>107.5 ns, 80 B</b> — the
    ///         stale "8.1 KB @0 subscribers" row for this same bus shape, re-measured
    ///         (75× faster, 100× less allocated). See docs/BENCHMARKS.md §5.3.3–§5.3.4.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_0Sub_ScrollbackOn")]
    public Task PublishAsync_0Sub_ScrollbackOn() => _enqueueOnlyBus.PublishAsync(_event);

    /// <summary>
    ///     Op: enqueue + drain with <b>one</b> subscriber whose handler returns an
    ///     already-completed <see cref="ValueTask" />. The consumer is drained inside
    ///     the measured region — <c>PublishAsync</c> does not return until the
    ///     handler has run.
    ///     <para>
    ///         Await semantics: synchronous throughout
    ///         (<c>DispatchToOneAsync</c> takes the <c>IsCompletedSuccessfully</c>
    ///         branch, InMemoryEventBus.cs:721); nothing suspends.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: one <c>Task&lt;ValueTuple&lt;bool,
    ///         AgentEvent&gt;&gt;</c> from <c>RunMiddlewareAsync</c> plus one
    ///         <c>Task&lt;ValueTuple&lt;DispatchOutcome, Task?&gt;&gt;</c> from
    ///         <c>DispatchToOneAsync</c> (InMemoryEventBus.cs:706, result set at
    ///         :716/:723) — the <c>Task</c> state machines themselves are structs and
    ///         never box because nothing suspends. The handler budget CTS is rented
    ///         from the <c>#249</c> single-slot pool (InMemoryEventBus.cs:680) and
    ///         returned at :690; the per-publish <c>CancelAfter</c> (:719) makes
    ///         <c>CancellationTokenSource.TryReset</c> refuse the instance, so the pool
    ///         does not recycle it and one CTS is allocated per fan-out publish.
    ///     </para>
    ///     <para>
    ///         Measured 2026-09-28 in CI (run 36452709074): <b>242.7 ns, 200 B</b>
    ///         = 80 (middleware result task) + 40 (CTS) + 80 (dispatch result task).
    ///         See docs/BENCHMARKS.md §5.3.3.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_1Sub")]
    public Task PublishAsync_1Sub() => _enqueueAndDrainBus.PublishAsync(_event);

    /// <summary>
    ///     Op: drain only — the 1-subscriber fan-out with scrollback switched off,
    ///     so the ring append and its lock are excluded. Subtract this row from
    ///     <see cref="PublishAsync_1Sub" /> to price the enqueue; subtract it from
    ///     <see cref="PublishAsync_0Sub_ScrollbackOff" /> to price the drain.
    ///     <para>
    ///         Await semantics: synchronous throughout, as above.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: as <see cref="PublishAsync_1Sub" /> minus the
    ///         <c>RunMiddlewareAsync</c> task (the middleware pipeline is still
    ///         awaited even with an empty ring) — the
    ///         <c>Task&lt;ValueTuple&lt;bool, AgentEvent&gt;&gt;</c> is therefore
    ///         <b>not</b> avoidable by disabling scrollback; only the ring append is.
    ///         Confirmed by measurement: this row and <see cref="PublishAsync_1Sub" />
    ///         both report 200 B (2026-09-28 CI, run 36452709074) — the ring append
    ///         costs time and no bytes.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_1Sub_ScrollbackOff")]
    public Task PublishAsync_1Sub_ScrollbackOff() => _drainOnlyBus.PublishAsync(_event);

    /// <summary>
    ///     Op: enqueue + drain with one subscriber that genuinely suspends, to
    ///     separate "the awaited work was already done" from "the await cost a real
    ///     suspension". This is the only case here that enters
    ///     <c>DispatchToOneAsync</c>'s <c>!IsCompletedSuccessfully</c> branch
    ///     (InMemoryEventBus.cs:721) and its <c>dispatch.AsTask()</c> conversion.
    ///     <para>
    ///         Await semantics: one real suspension + thread-pool resumption per
    ///         publish. The drain is still fully awaited before
    ///         <see cref="InMemoryEventBus.PublishAsync" /> returns (the
    ///         <c>ct.CanBeCanceled == false</c> direct-await branch,
    ///         InMemoryEventBus.cs:727) — no handler outlives the measured region,
    ///         so the next iteration cannot overlap the previous one.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: adds the <c>Task</c> materialised by
    ///         <c>ValueTask.AsTask()</c> (InMemoryEventBus.cs:726) on top of the
    ///         <see cref="PublishAsync_1Sub" /> attribution. This row is the input
    ///         #47/S4 needs for the <c>ValueTask</c>-shape question; do not read it as
    ///         a regression against the synchronous rows. Measured 2026-09-28 in CI
    ///         (run 36452709074): 8.06 µs / 760 B, of which the 8 µs and most of the
    ///         extra 560 B are the benchmark's own <c>Task.Yield()</c> handler, not bus
    ///         cost — see docs/BENCHMARKS.md §5.3.3.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_1Sub_AsyncDrain")]
    public Task PublishAsync_1Sub_AsyncDrain() => _asyncDrainBus.PublishAsync(_event);

    /// <summary>Consumer that completes synchronously — the common real case.</summary>
    private static ValueTask SynchronousConsumer(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Consumer that suspends exactly once before completing.</summary>
    private static async ValueTask AsynchronousConsumer(AgentEvent evt, CancellationToken ct) => await Task.Yield();

    /// <summary>
    ///     Fill a bus's scrollback ring to capacity so measured iterations take
    ///     the steady overwrite branch. Runs before any consumer is attached, so
    ///     it can neither suspend nor dispatch.
    /// </summary>
    private static void PrimeRing(InMemoryEventBus bus)
    {
        for (int i = 0; i < ScrollbackCapacity; i++)
        {
            bus.PublishAsync(new TurnStartEvent(i)).GetAwaiter().GetResult();
        }
    }
}

/// <summary>
///     Fan-out scale for the same publish path (#391). Split out of
///     <see cref="EventBusBenchmark" /> — and therefore part of
///     <c>EventBusBenchmark.cs</c> — only so that the class-level
///     <see cref="SubscriberCount" /> parameter does not multiply the 0-subscriber
///     and 1-subscriber rows of that class with meaningless duplicates. Everything
///     else is shared with <see cref="EventBusBenchmark" />.
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <see cref="InMemoryEventBus.PublishAsync" /> of a
///         single <see cref="TurnStartEvent" /> fanned out to <c>SubscriberCount</c>
///         handlers; the retention-on row additionally appends to the scrollback ring.</item>
///         <item><c>Payload:</c> 1 event — no session/message ids, no text body, no tool
///         args. Built once in <c>Setup</c>; no row allocates its event payload.</item>
///         <item><c>StateReset:</c> none, by construction — see
///         <see cref="EventBusBenchmark" />. The ring overwrite is idempotent (same
///         immutable event instance republished) and the #47 queue-age counters return
///         to quiescent, so nothing accumulates and an <c>[IterationCleanup]</c> would
///         be dead code.</item>
///         <item><c>Drain:</c> all <c>SubscriberCount</c> handlers are awaited before the
///         measured call returns — the fan-out row is enqueue <i>and</i> drain, never
///         enqueue-only.</item>
///         <item><c>RetainedState:</c> the scrollback ring on the retention-on bus,
///         pre-filled to capacity in <c>Setup</c> so iterations take the steady
///         overwrite branch. The bus has no drain API, so the ring stays warm by
///         design; that is part of the measured cost.</item>
///         <item><c>AwaitSemantics:</c> both rows return the <c>Task</c> the bus
///         produced, and complete synchronously — every handler returns an already
///         completed <see cref="ValueTask" />, so <c>DispatchToOneAsync</c> never
///         leaves its <c>IsCompletedSuccessfully</c> branch and nothing suspends. The
///         immutable-array subscription snapshot is a struct copy, so it costs no
///         allocation.</item>
///         <item><c>AllocAttribution:</c> one
///         <c>Task&lt;ValueTuple&lt;bool, AgentEvent&gt;&gt;</c> per publish plus one
///         <c>Task&lt;ValueTuple&lt;DispatchOutcome, Task?&gt;&gt;</c> per subscriber
///         plus the per-publish budget CTS — i.e. the measured column scales with
///         <c>SubscriberCount</c>. The dead-subscriber buffer is
///         <c>ArrayPool</c>-rented and only taken when a subscriber actually dies
///         (<c>MarkDead</c>, <c>InMemoryEventBus.cs:598</c>), and
///         <c>DispatchToSubscribersAsync</c>'s local functions are struct closures, so
///         neither contributes here. Measured values: <c>docs/BENCHMARKS.md</c> §5.4.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusBenchmarkFanout
{
    /// <summary>
    ///     Scrollback capacity for the retention-on bus. Kept identical to
    ///     <see cref="EventBusBenchmark" />'s 1024 so the two classes' enqueue-side
    ///     rows price the same enqueue.
    /// </summary>
    private const int ScrollbackCapacity = 1024;

    /// <summary>Number of attached subscribers.</summary>
    [Params(10, 100)]
    public int SubscriberCount { get; set; }

    /// <summary>Scrollback on, <see cref="SubscriberCount" /> subscribers.</summary>
    private InMemoryEventBus _enqueueAndDrainBus = null!;

    /// <summary>Scrollback off, <see cref="SubscriberCount" /> subscribers.</summary>
    private InMemoryEventBus _drainOnlyBus = null!;

    /// <summary>Payload built once; see <see cref="EventBusBenchmark" />.</summary>
    private AgentEvent _event = null!;

    [GlobalSetup]
    public void Setup()
    {
        _event = new TurnStartEvent(42);

        _drainOnlyBus = new InMemoryEventBus(maxScrollback: 0);
        _enqueueAndDrainBus = new InMemoryEventBus(maxScrollback: ScrollbackCapacity);

        for (int i = 0; i < ScrollbackCapacity; i++)
        {
            _enqueueAndDrainBus.PublishAsync(new TurnStartEvent(i)).GetAwaiter().GetResult();
        }

        for (int i = 0; i < SubscriberCount; i++)
        {
            _enqueueAndDrainBus.Subscribe(SynchronousConsumer);
            _drainOnlyBus.Subscribe(SynchronousConsumer);
        }
    }

    /// <summary>
    ///     Op: enqueue + drain to <see cref="SubscriberCount" /> subscribers,
    ///     each drained inside the measured region.
    ///     <para>
    ///         Await semantics: synchronous throughout (handlers return a completed
    ///         <see cref="ValueTask" />, so <c>DispatchToOneAsync</c> never leaves its
    ///         <c>IsCompletedSuccessfully</c> branch). The immutable-array snapshot
    ///         taken at InMemoryEventBus.cs:263 is a struct copy — no allocation.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: one
    ///         <c>Task&lt;ValueTuple&lt;bool, AgentEvent&gt;&gt;</c> for the whole
    ///         publish (<c>RunMiddlewareAsync</c>) plus <b>one</b>
    ///         <c>Task&lt;ValueTuple&lt;DispatchOutcome, Task?&gt;&gt;</c> per
    ///         subscriber (<c>DispatchToOneAsync</c>) — i.e. the expected column
    ///         scales with <see cref="SubscriberCount" />, not with a fixed 8 KB. The
    ///         dead-subscriber buffer is <c>ArrayPool</c>-rented and only taken when a
    ///         subscriber actually dies
    ///         (<c>MarkDead</c>, InMemoryEventBus.cs:598), and
    ///         <c>DispatchToSubscribersAsync</c>'s own local functions are struct
    ///         closures, so neither contributes here.
    ///     </para>
    ///     <para>
    ///         Measured 2026-09-28 in CI (run 36452709074): <b>920 B @10,
    ///         8120 B @100</b> — exactly 80 B per subscriber on top of a constant
    ///         120 B per publish, and no longer a flat 8 KB. See
    ///         docs/BENCHMARKS.md §5.3.3.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_NSub")]
    public Task PublishAsync_NSub() => _enqueueAndDrainBus.PublishAsync(_event);

    /// <summary>
    ///     Op: drain to <see cref="SubscriberCount" /> subscribers with scrollback
    ///     off — fan-out cost without the ring append.
    ///     <para>
    ///         Await semantics / payload / state reset / retained: as
    ///         <see cref="PublishAsync_NSub" />.
    ///     </para>
    ///     <para>
    ///         Allocation attribution: <see cref="PublishAsync_NSub" /> minus the ring
    ///         append; the per-subscriber
    ///         <c>Task&lt;ValueTuple&lt;DispatchOutcome, Task?&gt;&gt;</c> remains.
    ///     </para>
    /// </summary>
    [Benchmark(Description = "PublishAsync_NSub_ScrollbackOff")]
    public Task PublishAsync_NSub_ScrollbackOff() => _drainOnlyBus.PublishAsync(_event);

    private static ValueTask SynchronousConsumer(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
///     #408 delivery-contract split: the same publish workload measured as three
///     separate rows so a reader can answer "what does this number include?"
///     without guessing whether consumer drain is folded into it.
///     <list type="bullet">
///         <item><see cref="EnqueueOnly" /> — accept + retain only, <b>no consumer is
///         attached at all</b>. It measures NOTHING about delivery.</item>
///         <item><see cref="EnqueueAndDrainConsumer" /> — the same publish with
///         <c>SubscriberCount</c> handlers attached; the consumer IS awaited.</item>
///         <item><see cref="SteadyState" /> — the live-session loop: awaited publish +
///         awaited delivery + a scrollback tail read.</item>
///     </list>
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> <c>PublishAsync</c> of one <see cref="TurnStartEvent" />
///         (plus, for <c>SteadyState</c>, one <c>GetScrollback</c> tail read).</item>
///         <item><c>Payload:</c> 1 event — no session/message ids, no text body.</item>
///         <item><c>StateReset:</c> none. All three buses are built once in <c>Setup</c> and
///         are never reset between iterations; the scrollback ring cannot be drained
///         (<c>InMemoryEventBus</c> has no API for it).</item>
///         <item><c>Drain:</c> none needed — <c>EnqueueAndDrainConsumer</c> and
///         <c>SteadyState</c> finish only once every handler has returned;
///         <c>EnqueueOnly</c> has no consumer, so there is nothing to drain.</item>
///         <item><c>RetainedState:</c> the scrollback ring on all three buses — it saturates
///         after <c>MaxScrollback</c> publishes and is never emptied (overwrite cost equals
///         fresh-slot cost, so the rows stay stable once warm).</item>
///         <item><c>AwaitSemantics:</c> every row <c>await</c>s the publish it issues;
///         <c>EnqueueOnly</c> awaits a bus with zero subscribers, so the await completes
///         synchronously and measures the accept path alone. <c>SteadyState</c> additionally
///         awaits the tail read.</item>
///         <item><c>AllocAttribution:</c> ring append + counters. The delta between
///         <c>EnqueueOnly</c> and <c>EnqueueAndDrainConsumer</c> is exactly the fan-out
///         (10 handler invocations, still allocation-free for completed handlers);
///         <c>SteadyState</c> adds the tail-copy array.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusDeliveryBenchmark
{
    private const int MaxScrollback = 1024;

    // Fixed (not [Params]) so the three rows stay directly comparable: the
    // subscriber-count curve is EventBusBenchmark's job.
    private const int SubscriberCount = 10;

    private InMemoryEventBus _drainBus = null!;
    private InMemoryEventBus _enqueueBus = null!;
    private InMemoryEventBus _steadyBus = null!;
    private TurnStartEvent _event = null!;

    [GlobalSetup]
    public void Setup()
    {
        _event = new TurnStartEvent(7);

        // No subscriber: this bus can only accept + retain.
        _enqueueBus = new InMemoryEventBus(maxScrollback: MaxScrollback);

        // Same bus, but with consumers attached — the publish awaits them.
        _drainBus = new InMemoryEventBus(maxScrollback: MaxScrollback);
        for (int i = 0; i < SubscriberCount; i++)
        {
            _drainBus.Subscribe(NoOpHandler);
        }

        // Pre-fill the ring so SteadyState measures the saturated, overwrite
        // path a live session actually runs on (not a cold ring).
        _steadyBus = new InMemoryEventBus(maxScrollback: MaxScrollback);
        for (int i = 0; i < SubscriberCount; i++)
        {
            _steadyBus.Subscribe(NoOpHandler);
        }

        for (int i = 0; i < MaxScrollback; i++)
        {
            _steadyBus.PublishAsync(new TurnStartEvent(i)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    ///     Accept + retain only. This bus has <b>zero subscribers</b>, so nothing is
    ///     delivered and nothing is fanned out: the number is the ring-append cost
    ///     alone and says <b>nothing about delivery</b> — use it to price the queue,
    ///     never to price the bus end-to-end.
    /// </summary>
    [Benchmark(Description = "EnqueueOnly (no subscriber attached; measures nothing about delivery)")]
    public Task EnqueueOnly() => _enqueueBus.PublishAsync(_event);

    /// <summary>
    ///     Accept + retain + fan-out to <c>SubscriberCount</c> handlers. The consumer is
    ///     <b>awaited</b>: the returned task completes only after all 10 handlers have run.
    /// </summary>
    [Benchmark(Description = "EnqueueAndDrainConsumer (10 subscribers awaited)")]
    public Task EnqueueAndDrainConsumer() => _drainBus.PublishAsync(_event);

    /// <summary>
    ///     Steady state of a live session on a saturated ring: publish, await the
    ///     consumers, then read the scrollback tail. The consumer is <b>awaited</b>;
    ///     this is the only row that includes the copy-out of the retained ring.
    /// </summary>
    [Benchmark(Description = "SteadyState (saturated ring: awaited publish + tail read)")]
    public async Task<int> SteadyState()
    {
        await _steadyBus.PublishAsync(_event).ConfigureAwait(false);
        return _steadyBus.GetScrollback(MaxScrollback).Count;
    }

    private static ValueTask NoOpHandler(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
}
