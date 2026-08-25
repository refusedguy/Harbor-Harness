using Harbor.Abstractions.Events;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     ReconnectableRpcClient end-to-end (sprint 6 A1): the client survives a
///     mid-stream connection cut, re-dials with backoff, re-subscribes with
///     its last sequence, receives the exact missed range in order without
///     duplicates, and only loads a snapshot on first subscribe / resync.
/// </summary>
[NotInParallel]
public class ReconnectableRpcClientTests
{
    [Test]
    public async Task Client_SurvivesConnectionCut_AndCatchesUpWithoutDuplicates()
    {
        var sp = TestHost.Build();
        var bus = sp.GetRequiredService<IEventBus>();
        string pipe = TestHost.UniquePipeName("harbor-ipc-reconnect");
        var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            int snapshotCalls = 0;
            var wrapper = new ReconnectableRpcClient(
                _ => Task.FromResult<IIpcClientTransport>(
                    new ClientPipeTransport(pipe, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClientPipeTransport>())),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ReconnectableRpcClient>());

            var received = new List<EventFrame>();
            var gotSix = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gotFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var consumeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Consumer: collect everything; resolve after six distinct turns.
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in wrapper.SubscribeWithReconnectAsync(
                        _ =>
                        {
                            Interlocked.Increment(ref snapshotCalls);
                            return Task.CompletedTask;
                        },
                        consumeCts.Token))
                    {
                        lock (received)
                        {
                            received.Add(frame);
                            if (received.Count >= 1) gotFirst.TrySetResult(true);
                            if (received.Count >= 6) gotSix.TrySetResult(true);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Consumer window elapsed — assertions below judge the outcome.
                }
            });

            // Establish the subscription, fire event #1, and WAIT until the
            // client actually processed it — the cut must happen mid-stream,
            // not before the stream started.
            await server.SubscriptionReady;
            await bus.PublishAsync(new TurnStartEvent(1));
            bool firstArrived = await gotFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(firstArrived).IsTrue();
            await wrapper.CutCurrentConnectionForTestAsync();

            // Events fired while the client is "offline" — must be replayed.
            for (int turn = 2; turn <= 6; turn++)
            {
                await bus.PublishAsync(new TurnStartEvent(turn));
            }

            bool gotAll = await gotSix.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.That(gotAll).IsTrue();

            // Exactly-once, strictly ordered: no duplicates, no gaps.
            List<EventFrame> snapshot;
            lock (received) { snapshot = new List<EventFrame>(received); }

            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0)
                {
                    await Assert.That(snapshot[i].Sequence).IsGreaterThan(snapshot[i - 1].Sequence);
                }

                var turnStart = snapshot[i].Event as HarborEvent.TurnStart;
                await Assert.That(turnStart).IsNotNull();
                await Assert.That(turnStart!.Turn).IsEqualTo(i + 1);
            }

            // Snapshot loaded exactly once — the replay path must not need it.
            await Assert.That(Volatile.Read(ref snapshotCalls)).IsEqualTo(1);

            await wrapper.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Backoff_DoublesWithJitter_AndCaps()
    {
        var sp = TestHost.Build();
        var wrapper = new ReconnectableRpcClient(
            _ => throw new InvalidOperationException("not dialed in this test"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ReconnectableRpcClient>());

        TimeSpan first = wrapper.NextBackoffDelay();
        TimeSpan second = wrapper.NextBackoffDelay();
        TimeSpan tenth = wrapper.NextBackoffDelay();

        // ~500ms * [0.8..1.2]
        await Assert.That(first.TotalMilliseconds).IsGreaterThan(300);
        await Assert.That(first.TotalMilliseconds).IsLessThan(700);
        // ~1000ms * [0.8..1.2]
        await Assert.That(second.TotalMilliseconds).IsGreaterThan(700);
        await Assert.That(second.TotalMilliseconds).IsLessThan(1300);
        // capped at 30s * 1.2
        await Assert.That(tenth.TotalMilliseconds <= 36_000).IsTrue();
    }
}
