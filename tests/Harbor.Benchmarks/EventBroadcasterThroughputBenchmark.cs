using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipelines;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"EventBroadcaster" /> throughput — the cost of
///     projecting <see cref=\"AgentEvent" />s to <see cref=\"HarborEventData\" />,
///     MessagePack-serializing them, and writing the framed envelopes to N
///     connected client streams. Measures the hot path of IPC event dispatch
///     under concurrent subscriber load.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class EventBroadcasterThroughputBenchmark
{

    [Params(4, 16, 64)]
    public int ClientCount;
    private EventBroadcaster _broadcaster = null!;
    private List<PipeStream> _clientStreams = null!;
    private InMemoryEventBus _eventBus = null!;
    private List<SemaphoreSlim> _writeLocks = null!;

    [GlobalSetup]
    public void Setup()
    {
        _eventBus = new InMemoryEventBus(maxScrollback: 1024);
        _broadcaster = new EventBroadcaster(_eventBus, NullLogger<EventBroadcaster>.Instance);
        _broadcaster.Start();

        _clientStreams = new List<PipeStream>(ClientCount);
        _writeLocks = new List<SemaphoreSlim>(ClientCount);

        for (int i = 0; i < ClientCount; i++)
        {
            var pipe = new Pipe();
            var stream = new PipeStream(pipe);
            var writeLock = new SemaphoreSlim(1, 1);
            _broadcaster.RegisterAsync(stream, writeLock, null, $"bench-{i}")
                .GetAwaiter().GetResult();
            _clientStreams.Add(stream);
            _writeLocks.Add(writeLock);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var stream in _clientStreams)
            stream.Dispose();
        foreach (var lockObj in _writeLocks)
            lockObj.Dispose();
        _broadcaster.DisposeAsync().AsTask().Wait();
    }

    [Benchmark(Description = "Broadcast 1000 TurnStartEvent to N clients")]
    public async Task Broadcast_TurnStartEvents()
    {
        const int eventCount = 1000;
        for (int i = 0; i < eventCount; i++)
        {
            await _eventBus.PublishAsync(new TurnStartEvent(i % 100)).ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "Broadcast 1000 MessageUpdateEvent to N clients")]
    public async Task Broadcast_MessageUpdateEvents()
    {
        const int eventCount = 1000;
        var partial = AssistantMessage.Empty("session-1", "stub-1");
        for (int i = 0; i < eventCount; i++)
        {
            await _eventBus.PublishAsync(new MessageUpdateEvent(
                new TextDeltaEvent("m1", $"Token {i} "),
                partial)).ConfigureAwait(false);
        }
    }
}
