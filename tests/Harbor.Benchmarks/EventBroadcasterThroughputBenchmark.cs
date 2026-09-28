using BenchmarkDotNet.Attributes;
using System.IO.Pipelines;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref="EventBroadcaster" /> throughput — the cost of
///     projecting <see cref="AgentEvent" />s to <see cref="HarborEventData" />,
///     MessagePack-serializing them, and writing the framed envelopes to N
///     connected client streams. Measures the hot path of IPC event dispatch
///     under concurrent subscriber load.
///     <para><b>Measurement contract (#408)</b> — what this number includes:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> 1000 bus publishes per row (payload size is the
///         <c>[Params]</c> client count — the per-event work is fixed).</item>
///         <item><c>Payload:</c> 1000 synthetic <see cref="TurnStartEvent" />s, or 1000
///         <see cref="MessageUpdateEvent" />s carrying a short text delta.</item>
///         <item><c>StateReset:</c> none per iteration — the broadcaster, the bus and the
///         <c>ClientCount</c> pipes are created once in <c>Setup</c>. <c>DrainClientPipes</c>
///         empties the pipes between iterations so pooled pipe memory from the previous
///         iteration cannot back-pressure the next one.</item>
///         <item><c>Drain:</c> <c>DrainClientPipes()</c> reads every client pipe to empty
///         between iterations; the rows themselves do not await delivery, which is exactly
///         what the <see cref="EventBroadcasterDeliveryBenchmark" /> split makes explicit.</item>
///         <item><c>RetainedState:</c> the broadcaster's 1000-envelope replay ring (saturates and
///         is never emptied) plus the per-client outbound queues.</item>
///         <item><c>AwaitSemantics:</c> the rows await the bus publish, which awaits the
///         broadcaster's <c>OnEventAsync</c> (projection + MessagePack + per-client enqueue).
///         The per-client writer task that performs the framed stream write is
///         <b>not</b> awaited — its output is drained between iterations instead.</item>
///         <item><c>AllocAttribution:</c> one MessagePack payload + one envelope per event, and
///         the client-count copy of the registration snapshot; the pipe bytes themselves are
///         pooled and are not attributed to the row.</item>
///     </list>
///     <para>
///         Because these rows stop at the enqueue, they must not be read as "the client
///         received the bytes". <see cref="EventBroadcasterDeliveryBenchmark" /> carries the
///         three-row enqueue / enqueue+drain / steady-state split for that question.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBroadcasterThroughputBenchmark
{
    private EventBroadcaster _broadcaster = null!;
    private InMemoryEventBus _eventBus = null!;
    private List<Pipe> _clientPipes = null!;
    private List<PipeStream> _clientStreams = null!;
    private List<SemaphoreSlim> _writeLocks = null!;

    [Params(4, 16, 64)]
    public int ClientCount;

    [GlobalSetup]
    public void Setup()
    {
        _eventBus = new InMemoryEventBus(maxScrollback: 1024);
        _broadcaster = new EventBroadcaster(_eventBus, NullLogger<EventBroadcaster>.Instance);
        _broadcaster.Start();

        _clientStreams = new List<PipeStream>(ClientCount);
        _clientPipes = new List<Pipe>(ClientCount);
        _writeLocks = new List<SemaphoreSlim>(ClientCount);

        for (int i = 0; i < ClientCount; i++)
        {
            var pipe = new Pipe();
            var stream = new PipeStream(pipe);
            var writeLock = new SemaphoreSlim(1, 1);
            _broadcaster.RegisterAsync(stream, writeLock, lastSequence: null, clientId: $"bench-{i}")
                .GetAwaiter().GetResult();
            _clientPipes.Add(pipe);
            _clientStreams.Add(stream);
            _writeLocks.Add(writeLock);
        }
    }

    [IterationCleanup]
    public void DrainClientPipes()
    {
        // Nobody reads the client side: the broadcaster's per-client writer
        // tasks buffer framed envelopes into the pipes. Drain between iterations
        // so pooled pipe memory does not accumulate and contaminate later ones.
        foreach (var pipe in _clientPipes)
        {
            while (pipe.Reader.TryRead(out var result))
            {
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.Buffer.Length == 0)
                    break;
            }
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

/// <summary>
///     #408 delivery-contract split for <see cref="EventBroadcaster" />: the same
///     publish burst reported as enqueue-only, enqueue + awaited client drain, and
///     the steady-state burst/drain loop — three rows instead of one blended number.
///     <list type="bullet">
///         <item><see cref="EnqueueOnly" /> — publish the burst and stop. The per-client
///         writer tasks are <b>not awaited</b> and the pipes are not drained inside the op:
///         it measures <b>nothing about delivery to a client</b>.</item>
///         <item><see cref="EnqueueAndDrainConsumer" /> — publish the burst, then drain every
///         client pipe, so the consumer (the writer task) is effectively
///         <b>drained to completion</b> before the op returns.</item>
///         <item><see cref="SteadyState" /> — four burst+drain rounds with the broadcaster,
///         its replay ring and the pipes already warm.</item>
///     </list>
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> 1000 <c>PublishAsync</c> calls (one burst = one
///         <see cref="TurnStartEvent" /> per call), plus pipe draining where the row says so.</item>
///         <item><c>Payload:</c> 1000 short <see cref="TurnStartEvent" />s — the smallest event
///         the broadcaster projects, so the framing overhead is visible rather than hidden.</item>
///         <item><c>StateReset:</c> none per iteration. The broadcaster, the bus and the
///         <c>ClientCount</c> pipes are created once in <c>Setup</c>;
///         <c>DrainPipesBetweenIterations</c> empties the pipes between iterations so pooled
///         pipe memory from the previous iteration cannot back-pressure the next one.</item>
///         <item><c>Drain:</c> <c>EnqueueAndDrainConsumer</c> and <c>SteadyState</c> drain every
///         client pipe inside the measured op (bounded passes — see <c>MaxDrainPasses</c>);
///         <c>EnqueueOnly</c> deliberately does not, and says so.</item>
///         <item><c>RetainedState:</c> the broadcaster's 1000-envelope replay ring and the
///         per-client outbound queues; the pipes are emptied, never recreated.</item>
///         <item><c>AwaitSemantics:</c> every row awaits the bus publish, which awaits
///         <c>OnEventAsync</c> (projection + MessagePack + per-client enqueue). The
///         <c>EnqueueOnly</c> row explicitly does <b>not</b> await the per-client writer task;
///         the two drain rows do, via the bounded drain loop.</item>
///         <item><c>AllocAttribution:</c> one MessagePack payload + one envelope + one
///         registration-snapshot copy per event; the pipe transfer itself reuses pooled
///         buffers and shows up as time, not allocation.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EventBroadcasterDeliveryBenchmark
{
    private const int BurstSize = 1000;

    /// <summary>
    ///     Upper bound on drain passes. The drain loop exits as soon as three
    ///     consecutive passes move no bytes, so this only caps a pathological
    ///     writer that never flushes — it is not part of the measured cost.
    /// </summary>
    private const int MaxDrainPasses = 64;

    private EventBroadcaster _broadcaster = null!;
    private InMemoryEventBus _eventBus = null!;
    private List<Pipe> _clientPipes = null!;
    private List<PipeStream> _clientStreams = null!;
    private List<SemaphoreSlim> _writeLocks = null!;

    [Params(4, 16, 64)]
    public int ClientCount;

    [GlobalSetup]
    public void Setup()
    {
        _eventBus = new InMemoryEventBus(maxScrollback: 1024);
        _broadcaster = new EventBroadcaster(_eventBus, NullLogger<EventBroadcaster>.Instance);
        _broadcaster.Start();

        _clientStreams = new List<PipeStream>(ClientCount);
        _clientPipes = new List<Pipe>(ClientCount);
        _writeLocks = new List<SemaphoreSlim>(ClientCount);

        for (int i = 0; i < ClientCount; i++)
        {
            var pipe = new Pipe();
            var stream = new PipeStream(pipe);
            var writeLock = new SemaphoreSlim(1, 1);
            _broadcaster.RegisterAsync(stream, writeLock, lastSequence: null, clientId: $"bench-{i}")
                .GetAwaiter().GetResult();
            _clientPipes.Add(pipe);
            _clientStreams.Add(stream);
            _writeLocks.Add(writeLock);
        }
    }

    [IterationCleanup]
    public void DrainPipesBetweenIterations()
    {
        // Same rationale as EventBroadcasterThroughputBenchmark.DrainClientPipes:
        // nobody reads the client side, so anything left in the pipes from the
        // previous iteration must go before the next one is measured.
        foreach (var pipe in _clientPipes)
        {
            while (pipe.Reader.TryRead(out var result))
            {
                pipe.Reader.AdvanceTo(result.Buffer.End);
            }
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

    /// <summary>
    ///     Publish the burst and stop. The per-client writer tasks are <b>not awaited</b>
    ///     and the pipes are <b>not drained</b> inside this op: the row prices the
    ///     accept/serialize/enqueue path only and says <b>nothing about delivery to a
    ///     client</b>. Compare it with <see cref="EnqueueAndDrainConsumer" /> to see what
    ///     delivery actually costs.
    /// </summary>
    [Benchmark(Description = "EnqueueOnly (writer tasks not awaited; measures nothing about client delivery)")]
    public async Task EnqueueOnly()
    {
        for (int i = 0; i < BurstSize; i++)
        {
            await _eventBus.PublishAsync(new TurnStartEvent(i)).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Publish the burst, then drain every client pipe to empty so the consumer
    ///     (the per-client writer task) has flushed. The consumer is
    ///     <b>drained to completion</b> before the op returns.
    /// </summary>
    [Benchmark(Description = "EnqueueAndDrainConsumer (writer task drained to completion)")]
    public async Task<long> EnqueueAndDrainConsumer()
    {
        await PublishBurstAsync().ConfigureAwait(false);
        return await DrainAllAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Four burst+drain rounds with the broadcaster, its replay ring and the pipes
    ///     already warm — the operating loop of a live IPC session. The consumer is
    ///     <b>drained to completion</b> on every round.
    /// </summary>
    [Benchmark(Description = "SteadyState (4 warm burst+drain rounds)")]
    public async Task<long> SteadyState()
    {
        long total = 0;
        for (int round = 0; round < 4; round++)
        {
            await PublishBurstAsync().ConfigureAwait(false);
            total += await DrainAllAsync().ConfigureAwait(false);
        }

        return total;
    }

    private async Task PublishBurstAsync()
    {
        for (int i = 0; i < BurstSize; i++)
        {
            await _eventBus.PublishAsync(new TurnStartEvent(i)).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads every client pipe until three consecutive passes move no bytes,
    ///     yielding between passes so an in-flight writer task can flush. Bounded by
    ///     <see cref="MaxDrainPasses" />.
    /// </summary>
    private async Task<long> DrainAllAsync()
    {
        long total = 0;
        int idlePasses = 0;
        for (int pass = 0; pass < MaxDrainPasses && idlePasses < 3; pass++)
        {
            bool moved = false;
            foreach (var pipe in _clientPipes)
            {
                while (pipe.Reader.TryRead(out var result))
                {
                    total += result.Buffer.Length;
                    pipe.Reader.AdvanceTo(result.Buffer.End);
                    moved = true;
                }
            }

            if (moved)
            {
                idlePasses = 0;
                continue;
            }

            idlePasses++;
            await Task.Yield();
        }

        return total;
    }
}
