using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harbor.TestKit;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Multi-agent sprint: the broadcaster's turn tracking must be SESSION-SCOPED.
///     Two parallel runs interleave their TurnStart events; each session's emitted
///     TurnEnd must carry its OWN latest turn index — the shared counter the old
///     code kept leaked run A's turn into run B's events.
/// </summary>
[NotInParallel("ipc")]
public class BroadcasterTurnIsolationTests
{
    private static readonly AssistantMessage TurnAssistant =
        AssistantMessage.Empty("session-x", "test-model");

    private static readonly ToolResultMessage[] NoToolResults = [];

    [Test]
    [Timeout(60_000)]
    public async Task TwoInterleavedRuns_TurnEndCarriesOwnSessionTurn(CancellationToken cancellationToken)
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

            _ = Task.Run(async () => await client.SendAsync(new SubscribeToEventsRequest(), cancellationToken));
            await server.SubscriptionReady;

            const string runA = "session-a";
            const string runB = "session-b";

            // Interleave two runs so a shared turn counter would cross-contaminate:
            // A reaches turn 2, B reaches turn 3, and B's latest turn wins "last write".
            await bus.PublishAsync(TestEvents.AgentStart(runA));
            await bus.PublishAsync(TestEvents.AgentStart(runB));
            await bus.PublishAsync(TestEvents.TurnStart(sessionId: runA));
            await bus.PublishAsync(TestEvents.TurnStart(2, runA));
            await bus.PublishAsync(TestEvents.TurnStart(sessionId: runB));
            await bus.PublishAsync(TestEvents.TurnStart(3, runB));

            // Run B ends FIRST (turn 3), then run A (turn 2) — with the old shared
            // _currentTurn both TurnEnds would carry 3.
            await bus.PublishAsync(TestEvents.TurnEnd(TurnAssistant, NoToolResults, runB));
            await bus.PublishAsync(TestEvents.TurnEnd(TurnAssistant, NoToolResults, runA));

            // 2 AgentStart + 4 TurnStart + 2 TurnEnd = 8 projected frames.
            List<EventFrame> frames = await FrameCollector.CollectFramesAsync(client, 8, cancellationToken);

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
    [Timeout(60_000)]
    public async Task RunRestart_ResetsTurnPerSession(CancellationToken cancellationToken)
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

            _ = Task.Run(async () => await client.SendAsync(new SubscribeToEventsRequest(), cancellationToken));
            await server.SubscriptionReady;

            const string session = "session-reset";

            await bus.PublishAsync(TestEvents.AgentStart(session));
            await bus.PublishAsync(TestEvents.TurnStart(5, session));
            await bus.PublishAsync(TestEvents.TurnEnd(TurnAssistant, NoToolResults, session));

            // A fresh run in the SAME session restarts the turn counter at 1.
            await bus.PublishAsync(TestEvents.AgentStart(session));
            await bus.PublishAsync(TestEvents.TurnStart(sessionId: session));
            await bus.PublishAsync(TestEvents.TurnEnd(TurnAssistant, NoToolResults, session));

            // 2 AgentStart + 2 TurnStart + 2 TurnEnd = 6 projected frames.
            List<EventFrame> frames = await FrameCollector.CollectFramesAsync(client, 6, cancellationToken);

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
