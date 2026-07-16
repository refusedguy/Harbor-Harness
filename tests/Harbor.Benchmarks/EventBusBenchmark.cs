using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.Benchmarks;

/// <summary>
/// Benchmarks <see cref="InMemoryEventBus.PublishAsync"/> with varying
/// subscriber counts. The hot path is a lock-free snapshot read followed by
/// a fan-out to N handlers. The baseline (0 subscribers) measures the
/// channel-write overhead; the 10/100 cases measure fan-out cost.
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

        for (var i = 0; i < SubscriberCount; i++)
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
    public async Task PublishAsync()
    {
        await _bus.PublishAsync(_event).ConfigureAwait(false);
    }

    private static ValueTask NoOpHandler(AgentEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
}
