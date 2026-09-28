using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Benchmarks;
/// <summary>
///     #47/S3 — what the zero-subscriber fast path costs, and what disqualifies
///     a bus from taking it. Four rows over the same publish, differing only in
///     the composition: the first two are the qualifying cases, the last two are
///     the near-misses a regression would silently become.
///     <list type="bullet">
///         <item>
///             <c>Operation:</c> one <c>PublishAsync</c> of a single
///             <see cref="TurnStartEvent" />. Every row publishes the same event
///             on a bus built once in <c>Setup</c>.
///         </item>
///         <item>
///             <c>Payload:</c> 1 event — constructed once in <c>Setup</c>, never
///             per iteration. No session/message ids, no text body, no tool args.
///         </item>
///         <item>
///             <c>StateReset:</c> none. All four buses are built once and are
///             never reconfigured; every bus here has <c>maxScrollback: 0</c>, so
///             there is no ring to saturate and no retained event to evict.
///         </item>
///         <item>
///             <c>Drain:</c> none — every row has at most one no-op handler that
///             completes synchronously, so no handler task outlives the measured
///             call and nothing has to be drained afterwards.
///         </item>
///         <item>
///             <c>RetainedState:</c> four <c>Interlocked</c> counters per bus
///             (<c>PublishedCount</c>, <c>FastPathCount</c>,
///             <c>OptionalSinkDrainCount</c>, <c>OptionalSinkDropCount</c>). They
///             grow monotonically and are overwritten in place, so the cost they
///             add is a contended increment and nothing else.
///         </item>
///         <item>
///             <c>AwaitSemantics:</c> the rows return the publish <c>Task</c>
///             directly; BenchmarkDotNet awaits it. The two qualifying rows
///             return the cached completed task, so the await is a no-op; the
///             sink/subscriber rows run the full path synchronously to completion.
///         </item>
///         <item>
///             <c>AllocAttribution:</c> the first row is the zero-allocation
///             claim and must report 0 B/op — it returns
///             <c>Task.CompletedTask</c> after two <c>Interlocked</c>
///             increments and touches no collection. The second row adds the
///             optional-sink drain (an indexed loop over a
///             <c>ValueTask.FromResult</c> sampler) and must also report 0 B/op.
///             The third and fourth rows are the disqualified shapes and allocate
///             whatever the mandatory pipeline and the fan-out cost.
///         </item>
///     </list>
///     <para>
///         This class measures ACCEPTANCE cost, not delivery: the qualifying rows
///         deliver to nobody and say nothing about what a subscriber costs — that
///         is <c>EventBusBenchmark</c> / <c>EventBusDeliveryBenchmark</c>. The
///         allocation side of the 0 B/op claim is additionally pinned by
///         <c>EventBusFastPathTests</c> in <c>tests/Harbor.Core.Tests</c>, which
///         asserts it with <c>GC.GetAllocatedBytesForCurrentThread</c> rather
///         than trusting a benchmark report.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusFastPathBenchmark
{
    private InMemoryEventBus _qualifying = null!;
    private InMemoryEventBus _optionalDrain = null!;
    private InMemoryEventBus _mandatorySink = null!;
    private InMemoryEventBus _oneSubscriber = null!;
    private TurnStartEvent _event = null!;

    [GlobalSetup]
    public void Setup()
    {
        _event = new TurnStartEvent(42);

        // Qualifies: 0 subscribers, nothing to retain, no mandatory sink.
        _qualifying = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);

        // Qualifies too — a sampler is an OPTIONAL sink, so it is drained on the
        // fast path instead of blocking it. This is the case the pre-#47/S3
        // guard could not express at all (it required zero middleware).
        _optionalDrain = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            0,
            new IEventBusMiddleware[] { new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 1.0) });

        // Disqualified: a MANDATORY sink must see every event, so the full path
        // runs even with no subscribers and nothing to retain.
        _mandatorySink = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            0,
            new IEventBusMiddleware[] { new TypeFilterMiddleware(NullLogger<TypeFilterMiddleware>.Instance) });

        // Disqualified: one live subscriber, zero middleware, zero scrollback.
        _oneSubscriber = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);
        _oneSubscriber.Subscribe(static (_, _) => ValueTask.CompletedTask);
    }

    /// <summary>The qualifying composition. Must report 0 B/op.</summary>
    [Benchmark(Description = "Qualifying_0Sub_NoSinks_ScrollbackOff")]
    public Task Qualifying_0Sub_NoSinks_ScrollbackOff() => _qualifying.PublishAsync(_event);

    /// <summary>Qualifying composition with an optional sink drained inline. Must report 0 B/op.</summary>
    [Benchmark(Description = "Qualifying_0Sub_OptionalSinkDrained_ScrollbackOff")]
    public Task Qualifying_0Sub_OptionalSinkDrained_ScrollbackOff() => _optionalDrain.PublishAsync(_event);

    /// <summary>Near-miss: a mandatory sink forces the full path. Allocates by design.</summary>
    [Benchmark(Description = "Disqualified_0Sub_MandatorySink_ScrollbackOff")]
    public Task Disqualified_0Sub_MandatorySink_ScrollbackOff() => _mandatorySink.PublishAsync(_event);

    /// <summary>Near-miss: a live subscriber forces the full path. Allocates by design.</summary>
    [Benchmark(Description = "Disqualified_1Sub_NoSinks_ScrollbackOff")]
    public Task Disqualified_1Sub_NoSinks_ScrollbackOff() => _oneSubscriber.PublishAsync(_event);
}
