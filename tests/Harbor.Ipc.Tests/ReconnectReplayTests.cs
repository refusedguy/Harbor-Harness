using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harbor.E2E.Framework;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Reconnect replay semantics (sprint 6 A1): the server retains a ring of
///     the last 1000 envelopes; a reconnecting client that presents its last
///     processed sequence receives exactly the missed envelopes in order; a
///     gap larger than the ring yields ResyncRequired instead.
/// </summary>
[NotInParallel("ipc")]
[ParallelLimiter<MockServerLimit>]
public class ReconnectReplayTests
{
    private static async Task<(HarborIpcServer Server, IEventBus Bus, IServiceProvider Sp, string Pipe)> StartServer()
    {
        var sp = TestHost.Build();
        var bus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-replay");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        return (server, bus, sp, pipe);
    }

    /// <summary>Subscribe and drain until at least <paramref name="count"/> frames arrive.</summary>
    private static async Task<List<EventFrame>> CollectFramesAsync(
        MessagePackRpcClient client, int count, CancellationTokenSource cts)
    {
        var frames = new List<EventFrame>();
        await foreach (var frame in client.EventFrames.ReadAllAsync(cts.Token))
        {
            frames.Add(frame);
            if (frames.Count >= count) break;
        }

        return frames;
    }

    [Test]
    public async Task Reconnect_LastSequence_ReplaysExactMissedRange()
    {
        (var server, var bus, var sp, string pipe) = await StartServer();
        try
        {
            // Generation 1: subscribe, consume one event, remember its sequence,
            // then drop the connection without unsubscribing (network cut).
            (ulong lastSeen, _) = await SubscribeConsumeAndCutAsync(server, bus, sp, pipe);

            // While "offline": more events fire on the bus.
            for (int turn = 2; turn <= 6; turn++)
            {
                await bus.PublishAsync(new TurnStartEvent(turn));
            }

            // Generation 2: fresh connection presenting its last position.
            await ReconnectAndVerifyReplayAsync(sp, pipe, lastSeen);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Reconnect_GapLargerThanRing_RequestsResync()
    {
        (var server, var bus, var sp, string pipe) = await StartServer();
        try
        {
            // Fill the entire replay ring while nobody is subscribed: the
            // oldest retained envelope is far ahead of any stale client.
            for (int i = 0; i < EventBroadcaster.MaxReplayEnvelopes + 50; i++)
            {
                await bus.PublishAsync(new TurnStartEvent(i));
            }

            var lf = sp.GetRequiredService<ILoggerFactory>();
            var transport = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
            var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>());
            await client.ConnectAsync();

            var ackTcs = new TaskCompletionSource<HarborResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = Task.Run(async () =>
                ackTcs.TrySetResult(await client.SendAsync(new SubscribeToEventsRequest(lastSequence: 3), cts.Token)));

            var ack = (OkResponse)await ackTcs.Task;
            var ackData = WireCodec.DeserializeDomain<SubscriptionAck>(ack.Payload)!;

            await Assert.That(ackData.ResyncRequired).IsTrue();

            // No replay storm follows: nothing is delivered within the window.
            bool anyFrame = await TryReadAnyFrameAsync(client, TimeSpan.FromMilliseconds(500));
            await Assert.That(anyFrame).IsFalse();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<bool> TryReadAnyFrameAsync(MessagePackRpcClient client, TimeSpan window)
    {
        using var timeout = new CancellationTokenSource(window);
        try
        {
            await foreach (var _ in client.EventFrames.ReadAllAsync(timeout.Token))
            {
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // window elapsed with zero frames — the expected shape
        }
    }

    /// <summary>Generation 1: subscribe, consume one event, cut the wire.</summary>
    private static async Task<(ulong LastSeen, MessagePackRpcClient Client)> SubscribeConsumeAndCutAsync(
        HarborIpcServer server, IEventBus bus, IServiceProvider sp, string pipe)
    {
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var transport = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
        var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>());
        await client.ConnectAsync();

        var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () => await client.SendAsync(new SubscribeToEventsRequest(), subscribeCts.Token));
        await server.SubscriptionReady;

        await bus.PublishAsync(new TurnStartEvent(1));
        List<EventFrame> frames = await CollectFramesAsync(client, 1, subscribeCts);

        // Abrupt disconnect: dispose the transport underneath the RPC
        // client, exactly like an OS-level connection reset.
        await transport.DisposeAsync();
        return (frames[0].Sequence, client);
    }

    /// <summary>Generation 2: reconnect with LastSequence and verify ordered replay.</summary>
    private static async Task ReconnectAndVerifyReplayAsync(IServiceProvider sp, string pipe, ulong lastSeen)
    {
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var transport = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
        var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>());
        await client.ConnectAsync();

        var ackTcs = new TaskCompletionSource<HarborResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = Task.Run(async () =>
            ackTcs.TrySetResult(await client.SendAsync(
                new SubscribeToEventsRequest(lastSeen), replayCts.Token)));

        var ack = (OkResponse)await ackTcs.Task;
        var ackData = WireCodec.DeserializeDomain<SubscriptionAck>(ack.Payload)!;
        await Assert.That(ackData.ResyncRequired).IsFalse();

        // Exactly the five missed turns arrive, in order.
        List<EventFrame> replayed = await CollectFramesAsync(client, 5, replayCts);
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(replayed[i].Sequence).IsEqualTo(lastSeen + (ulong)(i + 1));
            var turnStart = replayed[i].Event as HarborEvent.TurnStart;
            await Assert.That(turnStart).IsNotNull();
            await Assert.That(turnStart!.Turn).IsEqualTo(i + 2);
        }
    }
}
