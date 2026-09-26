using Harbor.Abstractions.Events;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Broadcast fan-out: one published event must reach EVERY subscribed
///     client. Two real pipe clients subscribe; a single session-less
///     <see cref="TurnStartEvent" /> (unleased ⇒ broadcast) is published;
///     both clients must receive the same envelope (same server sequence).
/// </summary>
[NotInParallel("ipc")]
public class BroadcasterFanoutTests
{
    [Test]
    public async Task TwoSubscribedClients_BothReceiveSameBroadcastEvent()
    {
        var sp = TestHost.Build();
        var bus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-fanout");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();

            var transportA = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
            var clientA = new MessagePackRpcClient(transportA, lf.CreateLogger<MessagePackRpcClient>());
            var transportB = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
            var clientB = new MessagePackRpcClient(transportB, lf.CreateLogger<MessagePackRpcClient>());
            try
            {
                await clientA.ConnectAsync();
                HarborResponse ackA = await clientA.SendAsync(new SubscribeToEventsRequest());
                await Assert.That(ackA is OkResponse).IsTrue();

                await clientB.ConnectAsync();
                HarborResponse ackB = await clientB.SendAsync(new SubscribeToEventsRequest());
                await Assert.That(ackB is OkResponse).IsTrue();

                // Session-less turn event: no lease owner ⇒ broadcast to all.
                await bus.PublishAsync(new TurnStartEvent(7));

                EventFrame? frameA = await ReadOneFrameWithin(clientA, TimeSpan.FromSeconds(10));
                EventFrame? frameB = await ReadOneFrameWithin(clientB, TimeSpan.FromSeconds(10));

                await Assert.That(frameA.HasValue).IsTrue();
                await Assert.That(frameB.HasValue).IsTrue();

                var turnA = frameA!.Value.Event as HarborEvent.TurnStart;
                var turnB = frameB!.Value.Event as HarborEvent.TurnStart;
                await Assert.That(turnA is not null).IsTrue();
                await Assert.That(turnB is not null).IsTrue();
                await Assert.That(turnA!.Turn).IsEqualTo(7);
                await Assert.That(turnB!.Turn).IsEqualTo(7);

                // Same envelope fanned out to both — identical server sequence.
                await Assert.That(frameA.Value.Sequence).IsEqualTo(frameB.Value.Sequence);
            }
            finally
            {
                await clientA.DisposeAsync();
                await clientB.DisposeAsync();
                await transportA.DisposeAsync();
                await transportB.DisposeAsync();
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<EventFrame?> ReadOneFrameWithin(MessagePackRpcClient client, TimeSpan window)
    {
        using var timeout = new CancellationTokenSource(window);
        try
        {
            await foreach (var frame in client.EventFrames.ReadAllAsync(timeout.Token).ConfigureAwait(false))
            {
                return frame;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
