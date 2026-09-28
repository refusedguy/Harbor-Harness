using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="InMemoryEventBus.PublishAsync" /> with varying
///     subscriber counts. The hot path is a lock-free snapshot read followed by
///     a fan-out to N handlers. The baseline (0 subscribers) measures the
///     channel-write overhead; the 10/100 cases measure fan-out cost.
///     <para><b>Measurement contract (#408)</b> — what this number includes:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <c>PublishAsync</c> of a single
///         <see cref="TurnStartEvent" />; <see cref="SubscriberCount" /> handlers are attached.</item>
///         <item><c>Payload:</c> 1 event — no session/message ids, no text body, no tool args.</item>
///         <item><c>StateReset:</c> none. The bus, its subscriber set and its 1024-slot
///         scrollback ring are built once in <c>Setup</c> and are NOT reset between iterations.</item>
///         <item><c>Drain:</c> none — the publish task completes only after every handler returned.</item>
///         <item><c>RetainedState:</c> the scrollback ring. It saturates after 1024 publishes
///         and <c>InMemoryEventBus</c> exposes no drain; in-place slot overwrite costs the
///         same as a fresh slot, so the row is stable once warm.</item>
///         <item><c>AwaitSemantics:</c> the op awaits the publish; the no-op handlers complete
///         synchronously, so no handler task outlives the measured call.</item>
///         <item><c>AllocAttribution:</c> ring append + Interlocked counters only — fan-out to
///         <c>ValueTask.CompletedTask</c> handlers allocates nothing.</item>
///     </list>
///     <para>
///         The enqueue-vs-delivery split lives in <see cref="EventBusDeliveryBenchmark" />;
///         this class is the fan-out scaling curve over subscriber count.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBusBenchmark
{
    private InMemoryEventBus _bus = null!;
    private AgentEvent _event = null!;

    [Params(0, 1, 10, 100)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bus = new InMemoryEventBus(maxScrollback: 1024);

        for (int i = 0; i < SubscriberCount; i++)
        {
            _bus.Subscribe(NoOpHandler);
        }

        _event = new TurnStartEvent(42);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // InMemoryEventBus does not implement IDisposable; subscribers are
        // eligible for GC once the bus instance is no longer rooted.
    }

    [Benchmark(Description = "PublishAsync")]
    public async Task PublishAsync() => await _bus.PublishAsync(_event).ConfigureAwait(false);

    private static ValueTask NoOpHandler(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
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
