using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.Core.Tests;

/// <summary>
///     Semantic tests for the live in-process event-topology contract declared
///     in <c>docs/EVENT_TOPOLOGY.md</c>. Covers only what is cheaply testable at
///     the bus level: per-producer ordering (G1), single observed sequence
///     (G2), awaited-publish delivery (G5), scrollback eviction, late-subscriber
///     future-only + unsubscribe/disposal. Higher-layer items (session
///     separation, IPC reconnect, projection purity) are explicitly out of
///     scope — see the doc §7.
/// </summary>
public class EventTopologySemanticsTests
{
    [Test]
    public async Task SingleProducer_OrderPreserved()
    {
        var bus = new InMemoryEventBus();
        var seen = new List<int>();
        bus.Subscribe(async (evt, ct) =>
        {
            seen.Add(((TurnStartEvent)evt).TurnIndex);
            await Task.CompletedTask;
        });

        for (int i = 0; i < 20; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        await Assert.That(seen.Count).IsEqualTo(20);
        for (int i = 0; i < 20; i++)
        {
            await Assert.That(seen[i]).IsEqualTo(i);
        }
    }

    [Test]
    public async Task SingleObservedSequence_AllSubscribersSeeSameOrder()
    {
        var bus = new InMemoryEventBus();
        var first = new List<int>();
        var second = new List<int>();
        bus.Subscribe(async (evt, ct) =>
        {
            first.Add(((TurnStartEvent)evt).TurnIndex);
            await Task.CompletedTask;
        });
        bus.Subscribe(async (evt, ct) =>
        {
            second.Add(((TurnStartEvent)evt).TurnIndex);
            await Task.CompletedTask;
        });

        for (int i = 0; i < 10; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        await Assert.That(first.Count).IsEqualTo(10);
        await Assert.That(second.Count).IsEqualTo(10);
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(second[i]).IsEqualTo(first[i]);
        }
    }

    [Test]
    public async Task AwaitedPublish_MeansDelivered()
    {
        var bus = new InMemoryEventBus();
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) =>
        {
            received.Add(evt);
            await Task.CompletedTask;
        });

        await bus.PublishAsync(new TurnStartEvent(1));
        await bus.PublishAsync(new TurnStartEvent(2));

        // No drain/grace period: when PublishAsync returns, fast subscribers
        // have processed the event (docs/EVENT_TOPOLOGY.md G5).
        await Assert.That(received.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Scrollback_EvictsOldest_NoLossBelowCapacity()
    {
        var bus = new InMemoryEventBus(maxScrollback: 3);
        for (int i = 0; i < 5; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        var tail = bus.GetScrollback(5);
        await Assert.That(tail.Count).IsEqualTo(3);
        await Assert.That(((TurnStartEvent)tail[0]).TurnIndex).IsEqualTo(2);
        await Assert.That(((TurnStartEvent)tail[2]).TurnIndex).IsEqualTo(4);
    }

    [Test]
    public async Task LateSubscriber_SeesFutureOnly_ScrollbackIsSnapshot()
    {
        var bus = new InMemoryEventBus(maxScrollback: 8);
        await bus.PublishAsync(new TurnStartEvent(0));
        await bus.PublishAsync(new TurnStartEvent(1));

        var late = new List<int>();
        bus.Subscribe(async (evt, ct) =>
        {
            late.Add(((TurnStartEvent)evt).TurnIndex);
            await Task.CompletedTask;
        });

        await bus.PublishAsync(new TurnStartEvent(2));

        // Subscribe never replays: only the post-subscription event arrives.
        await Assert.That(late.Count).IsEqualTo(1);
        await Assert.That(late[0]).IsEqualTo(2);

        // History is an explicit pull, and reads are repeatable snapshots.
        var first = bus.GetScrollback(8);
        var second = bus.GetScrollback(8);
        await Assert.That(first.Count).IsEqualTo(3);
        await Assert.That(second.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Unsubscribe_StopsFutureEvents_IdempotentDispose()
    {
        var bus = new InMemoryEventBus();
        var received = new List<AgentEvent>();
        var sub = bus.Subscribe(async (evt, ct) =>
        {
            received.Add(evt);
            await Task.CompletedTask;
        });

        await bus.PublishAsync(new TurnStartEvent(1));
        sub.Dispose();
        sub.Dispose(); // idempotent — must not throw
        await bus.PublishAsync(new TurnStartEvent(2));

        await Assert.That(received.Count).IsEqualTo(1);
    }
}
