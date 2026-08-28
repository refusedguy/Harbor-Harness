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
///     Parallel-publish contention benchmark. Four concurrent publishers each
///     push 250 events through the same <see cref="InMemoryEventBus"/> — the
///     scrollback lock is contended on every publish. Uses <c>Task.WhenAll</c>
///     over <c>Task.Run</c> workers so BenchmarkDotNet observes real thread
///     contention rather than cooperative async interleaving alone.
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
