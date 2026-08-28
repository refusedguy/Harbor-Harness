using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Multi-agent sprint: the broadcaster's turn tracking must be SESSION-SCOPED.
///     Two parallel runs interleave their TurnStart events; each session's emitted
///     TurnEnd must carry its OWN latest turn index — the shared counter the old
///     code kept leaked run A's turn into run B's events.
/// </summary>
[NotInParallel]
public class BroadcasterTurnIsolationTests
{
    private static readonly AssistantMessage TurnAssistant =
        AssistantMessage.Empty("session-x", "test-model");

    private static readonly ToolResultMessage[] NoToolResults = [];

    /// <summary>Subscribe and drain until at least <paramref name="count" /> frames arrive.</summary>
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
    public async Task TwoInterleavedRuns_TurnEndCarriesOwnSessionTurn()
    {
        var sp = TestHost.Build();
        var bus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-turniso");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var transport = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
            var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>());
            await client.ConnectAsync();

            var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = Task.Run(async () => await client.SendAsync(new SubscribeToEventsRequest(), subscribeCts.Token));
            await server.SubscriptionReady;

            const string runA = "session-a";
            const string runB = "session-b";

            // Interleave two runs so a shared turn counter would cross-contaminate:
            // A reaches turn 2, B reaches turn 3, and B's latest turn wins "last write".
            await bus.PublishAsync(new AgentStartEvent(runA, []));
            await bus.PublishAsync(new AgentStartEvent(runB, []));
            await bus.PublishAsync(new TurnStartEvent(1, runA));
            await bus.PublishAsync(new TurnStartEvent(2, runA));
            await bus.PublishAsync(new TurnStartEvent(1, runB));
            await bus.PublishAsync(new TurnStartEvent(3, runB));

            // Run B ends FIRST (turn 3), then run A (turn 2) — with the old shared
            // _currentTurn both TurnEnds would carry 3.
            await bus.PublishAsync(new TurnEndEvent(TurnAssistant, NoToolResults, runB));
            await bus.PublishAsync(new TurnEndEvent(TurnAssistant, NoToolResults, runA));

            // 2 AgentStart + 4 TurnStart + 2 TurnEnd = 8 projected frames.
            List<EventFrame> frames = await CollectFramesAsync(client, 8, subscribeCts);

            var endB = frames[6].Event as HarborEvent.TurnEnd;
            var endA = frames[7].Event as HarborEvent.TurnEnd;
            await Assert.That(endB).IsNotNull();
            await Assert.That(endA).IsNotNull();
            await Assert.That(endB!.Turn).IsEqualTo(3);
            await Assert.That(endA!.Turn).IsEqualTo(2);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task RunRestart_ResetsTurnPerSession()
    {
        var sp = TestHost.Build();
        var bus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-turnreset");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var transport = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
            var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>());
            await client.ConnectAsync();

            var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = Task.Run(async () => await client.SendAsync(new SubscribeToEventsRequest(), subscribeCts.Token));
            await server.SubscriptionReady;

            const string session = "session-reset";

            await bus.PublishAsync(new AgentStartEvent(session, []));
            await bus.PublishAsync(new TurnStartEvent(5, session));
            await bus.PublishAsync(new TurnEndEvent(TurnAssistant, NoToolResults, session));

            // A fresh run in the SAME session restarts the turn counter at 1.
            await bus.PublishAsync(new AgentStartEvent(session, []));
            await bus.PublishAsync(new TurnStartEvent(1, session));
            await bus.PublishAsync(new TurnEndEvent(TurnAssistant, NoToolResults, session));

            // 2 AgentStart + 2 TurnStart + 2 TurnEnd = 6 projected frames.
            List<EventFrame> frames = await CollectFramesAsync(client, 6, subscribeCts);

            var firstEnd = frames[2].Event as HarborEvent.TurnEnd;
            var secondEnd = frames[5].Event as HarborEvent.TurnEnd;
            await Assert.That(firstEnd).IsNotNull();
            await Assert.That(secondEnd).IsNotNull();
            await Assert.That(firstEnd!.Turn).IsEqualTo(5);
            await Assert.That(secondEnd!.Turn).IsEqualTo(1);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
