using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;

namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref="InMemoryEventBus.GetScrollback"/> — the cold
///     diagnostic path not covered by <c>EventBusBenchmark</c> (which measures
///     <c>PublishAsync</c>). Scrollback is a fixed-capacity ring buffer;
///     <c>GetScrollback</c> copies the requested tail under a short lock into
///     an exact-size array. Cost scales with <c>maxEvents</c> and with ring
///     contention (publishers overwriting slots concurrently).
///     <para><b>Measurement contract (#408)</b> — what this number includes:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <c>GetScrollback(MaxEvents)</c> tail copy (or, for
///         <c>Publish_AfterFull</c>, one publish onto an already-full ring).</item>
///         <item><c>Payload:</c> up to <c>MaxEvents</c> pre-built <see cref="TurnStartEvent" />s
///         (ring pre-filled once in <c>Setup</c>) — no ids, no text body.</item>
///         <item><c>StateReset:</c> none. The two buses are built once and the ring is never
///         emptied — <c>InMemoryEventBus</c> exposes no drain — so every row reads the same
///         saturated ring and <c>Publish_AfterFull</c> always takes the overwrite path.</item>
///         <item><c>Drain:</c> none — a read completes inside the ring lock; <c>Publish_AfterFull</c>
///         has no subscriber, so there is nothing to drain.</item>
///         <item><c>RetainedState:</c> the 1000-slot ring (the copy-out array is freshly
///         allocated per read and is not retained).</item>
///         <item><c>AwaitSemantics:</c> the only async row awaits its publish, which completes
///         synchronously (no subscribers); reads are synchronous.</item>
///         <item><c>AllocAttribution:</c> reads allocate exactly the returned
///         <c>ToolDescriptor[]</c>-style copy array (bounded by <c>MaxEvents</c>);
///         <c>GetScrollback_Empty</c> allocates nothing; <c>Publish_AfterFull</c> allocates nothing.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusScrollbackBenchmark
{
    private InMemoryEventBus _bus = null!;
    private InMemoryEventBus _emptyBus = null!;

    [Params(10, 100, 1000)]
    public int MaxEvents { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bus = new InMemoryEventBus(maxScrollback: 1000);
        for (int i = 0; i < 1000; i++)
        {
            _bus.PublishAsync(new TurnStartEvent(i)).GetAwaiter().GetResult();
        }

        _emptyBus = new InMemoryEventBus(10);
    }

    /// <summary>Tail copy from a full ring — baseline for allocation/copy cost.</summary>
    [Benchmark(Description = "GetScrollback_Tail", Baseline = true)]
    public IReadOnlyList<AgentEvent> GetScrollback_Tail() => _bus.GetScrollback(MaxEvents);

    /// <summary>Empty bus — measures the early-exit fast path (no lock taken beyond check).</summary>
    [Benchmark(Description = "GetScrollback_Empty")]
    public IReadOnlyList<AgentEvent> GetScrollback_Empty() => _emptyBus.GetScrollback(10);

    /// <summary>
    ///     Two consecutive reads — ensures no drain (repeatable snapshot).
    ///     Second result is returned to prevent dead-code elimination.
    /// </summary>
    [Benchmark(Description = "GetScrollback_Repeatable")]
    public IReadOnlyList<AgentEvent> GetScrollback_Repeatable()
    {
        _ = _bus.GetScrollback(MaxEvents);
        return _bus.GetScrollback(MaxEvents);
    }

    /// <summary>
    ///     Publish when the ring is already at capacity — measures the overwrite
    ///     path (slot reuse under lock) without fan-out. Complements the tail-copy
    ///     read above; together they bound the scrollback contention cost.
    /// </summary>
    [Benchmark(Description = "Publish_AfterFull_TailCopyUnderLock")]
    public async Task Publish_AfterFull()
    {
        await _bus.PublishAsync(new TurnStartEvent(1001)).ConfigureAwait(false);
    }
}

/// <summary>
///     #408 delivery-contract split on a saturated scrollback ring: enqueue-only,
///     enqueue + awaited consumer drain, and the steady-state operating loop, each
///     as its own row. The publish-side fan-out curve is
///     <see cref="EventBusBenchmark" />; this class pins the ring-state dimension.
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <c>PublishAsync</c> (plus a <c>GetScrollback</c> tail
///         read for <c>SteadyState</c>) against a ring already holding 1000 events.</item>
///         <item><c>Payload:</c> 1 <see cref="TurnStartEvent" /> — no ids, no text body.</item>
///         <item><c>StateReset:</c> none. The ring is filled once in <c>Setup</c> and can never
///         be emptied (<c>InMemoryEventBus</c> has no drain), so all three rows always take the
///         overwrite path and stay directly comparable with each other.</item>
///         <item><c>Drain:</c> none — the consumer rows complete only after every handler has
///         returned; <c>EnqueueOnly</c> has no consumer, so there is nothing to drain.</item>
///         <item><c>RetainedState:</c> the saturated 1000-slot ring on both publish buses.</item>
///         <item><c>AwaitSemantics:</c> every row awaits the publish it issues;
///         <c>EnqueueOnly</c> awaits a bus with zero subscribers (synchronous completion).
///         <c>SteadyState</c> additionally awaits the tail read.</item>
///         <item><c>AllocAttribution:</c> ring slot overwrite + counters only for the publish
///         rows; <c>SteadyState</c> adds the tail-copy array sized <c>MaxScrollback</c>.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusScrollbackDeliveryBenchmark
{
    private const int MaxScrollback = 1000;

    // Fixed (not [Params]) so the three rows stay directly comparable — the
    // subscriber-count curve is EventBusBenchmark's job.
    private const int SubscriberCount = 10;

    private InMemoryEventBus _drainBus = null!;
    private InMemoryEventBus _enqueueBus = null!;
    private InMemoryEventBus _steadyBus = null!;
    private TurnStartEvent _event = null!;

    [GlobalSetup]
    public void Setup()
    {
        _event = new TurnStartEvent(1001);

        // Saturated ring, zero subscribers — accept + overwrite only.
        _enqueueBus = CreateSaturatedBus(withConsumers: false);

        // Saturated ring, 10 awaited consumers — accept + overwrite + fan-out.
        _drainBus = CreateSaturatedBus(withConsumers: true);

        // Saturated ring, 10 awaited consumers, plus the diagnostic tail read.
        _steadyBus = CreateSaturatedBus(withConsumers: true);
    }

    /// <summary>
    ///     Accept + ring overwrite only. This bus has <b>zero subscribers</b>, so nothing
    ///     is delivered: the number is the enqueue cost alone and says <b>nothing about
    ///     delivery</b> on a full ring.
    /// </summary>
    [Benchmark(Description = "EnqueueOnly (no subscriber; saturated ring; measures nothing about delivery)")]
    public Task EnqueueOnly() => _enqueueBus.PublishAsync(_event);

    /// <summary>
    ///     Accept + ring overwrite + fan-out to <c>SubscriberCount</c> handlers. The
    ///     consumer is <b>awaited</b>: the task completes only after all handlers returned.
    /// </summary>
    [Benchmark(Description = "EnqueueAndDrainConsumer (10 subscribers awaited; saturated ring)")]
    public Task EnqueueAndDrainConsumer() => _drainBus.PublishAsync(_event);

    /// <summary>
    ///     Steady state of a live session on a full ring: publish, await the consumers,
    ///     then read the retained tail. The consumer is <b>awaited</b>.
    /// </summary>
    [Benchmark(Description = "SteadyState (saturated ring: awaited publish + tail read)")]
    public async Task<int> SteadyState()
    {
        await _steadyBus.PublishAsync(_event).ConfigureAwait(false);
        return _steadyBus.GetScrollback(MaxScrollback).Count;
    }

    private static InMemoryEventBus CreateSaturatedBus(bool withConsumers)
    {
        var bus = new InMemoryEventBus(maxScrollback: MaxScrollback);
        if (withConsumers)
        {
            for (int i = 0; i < SubscriberCount; i++)
            {
                bus.Subscribe(NoOpHandler);
            }
        }

        for (int i = 0; i < MaxScrollback; i++)
        {
            bus.PublishAsync(new TurnStartEvent(i)).GetAwaiter().GetResult();
        }

        return bus;
    }

    private static ValueTask NoOpHandler(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
///     Parallel-publish contention benchmark. Four concurrent publishers each
///     push 250 events through the same <see cref="InMemoryEventBus"/> — the
///     scrollback lock is contended on every publish. Uses <c>Task.WhenAll</c>
///     over <c>Task.Run</c> workers so BenchmarkDotNet observes real thread
///     contention rather than cooperative async interleaving alone.
///     <para><b>Measurement contract (#408)</b> — what this number includes:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> 4 <c>Task.Run</c> publishers × 250 <c>PublishAsync</c>,
///         joined with <c>Task.WhenAll</c>.</item>
///         <item><c>Payload:</c> 1000 <see cref="TurnStartEvent" />s, no ids, no text body.</item>
///         <item><c>StateReset:</c> none — the bus is built once in <c>Setup</c> and its ring is
///         never emptied (<c>InMemoryEventBus</c> has no drain), so contention is measured
///         against a ring that saturates within the first op.</item>
///         <item><c>Drain:</c> none — zero subscribers are attached, so there is no consumer
///         to drain; this row deliberately measures lock contention only.</item>
///         <item><c>RetainedState:</c> the scrollback ring plus the per-publish Interlocked
///         counters, all hammered by 4 threads.</item>
///         <item><c>AwaitSemantics:</c> the op awaits <c>Task.WhenAll</c>, so all 4 workers and
///         their 1000 publishes have completed before the measured call returns; the task
///         scheduling cost of <c>Task.Run</c> is inside the measurement.</item>
///         <item><c>AllocAttribution:</c> 4 worker tasks + 4 state-machine boxes + the
///         <see cref="TurnStartEvent" />s; ring append and counters allocate nothing.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusContentionBenchmark
{
    private InMemoryEventBus _bus = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bus = new InMemoryEventBus(maxScrollback: 1000);
    }

    [Benchmark(Description = "Publish_Parallel_4x250")]
    public async Task Publish_Parallel_4x250()
    {
        var tasks = new Task[4];
        for (int t = 0; t < 4; t++)
        {
            int baseIdx = t * 250;
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < 250; i++)
                {
                    await _bus.PublishAsync(new TurnStartEvent(baseIdx + i)).ConfigureAwait(false);
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
