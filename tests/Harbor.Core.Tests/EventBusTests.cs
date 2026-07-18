using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
namespace Harbor.Core.Tests;
public class EventBusTests
{
    [Test]
    public async Task PublishAsync_DeliversTo_AllSubscribers()
    {
        var bus = new InMemoryEventBus();
        var received1 = new List<AgentEvent>();
        var received2 = new List<AgentEvent>();

        bus.Subscribe(async (evt, ct) => received1.Add(evt));
        bus.Subscribe(async (evt, ct) => received2.Add(evt));

        var testEvent = new TurnStartEvent(1);
        await bus.PublishAsync(testEvent);

        await Assert.That(received1.Count).IsEqualTo(1);
        await Assert.That(received2.Count).IsEqualTo(1);
        await Assert.That(received1[0]).IsEqualTo(testEvent);
    }

    [Test]
    public async Task Subscribe_TypedFilter_Works()
    {
        var bus = new InMemoryEventBus();
        var turnEvents = new List<TurnStartEvent>();
        var messageEvents = new List<MessageStartEvent>();

        bus.Subscribe<TurnStartEvent>(async (evt, ct) => turnEvents.Add(evt));
        bus.Subscribe<MessageStartEvent>(async (evt, ct) => messageEvents.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));
        await bus.PublishAsync(new TurnStartEvent(2));
        await bus.PublishAsync(new MessageStartEvent(AssistantMessage.Empty("s", "m")));

        await Assert.That(turnEvents.Count).IsEqualTo(2);
        await Assert.That(messageEvents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Unsubscribe_StopsReceivingEvents()
    {
        var bus = new InMemoryEventBus();
        var received = new List<AgentEvent>();

        var sub = bus.Subscribe(async (evt, ct) => received.Add(evt));
        await bus.PublishAsync(new TurnStartEvent(1));

        sub.Dispose();
        await bus.PublishAsync(new TurnStartEvent(2));

        await Assert.That(received.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetScrollback_ReturnsRecentEvents()
    {
        var bus = new InMemoryEventBus(maxScrollback: 5);
        for (int i = 0; i < 10; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        var scrollback = bus.GetScrollback(3);
        await Assert.That(scrollback.Count).IsEqualTo(3);
        await Assert.That(((TurnStartEvent)scrollback[0]).TurnIndex).IsEqualTo(7);
        await Assert.That(((TurnStartEvent)scrollback[2]).TurnIndex).IsEqualTo(9);
    }

    /// <summary>
    ///     Architecture audit v2 §PERF-008 regression test: GetScrollback must
    ///     NOT drain the buffer — a second late subscriber should see the same
    ///     history. The previous Channel-based implementation emptied the
    ///     channel on every read; this test would have failed (second call
    ///     returned Count=0).
    /// </summary>
    [Test]
    public async Task GetScrollback_DoesNotDrainBuffer_SecondCallSeesSameHistory()
    {
        var bus = new InMemoryEventBus(maxScrollback: 8);
        for (int i = 0; i < 5; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        var first = bus.GetScrollback(5);
        var second = bus.GetScrollback(5);

        await Assert.That(first.Count).IsEqualTo(5);
        await Assert.That(second.Count).IsEqualTo(5);
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(((TurnStartEvent)second[i]).TurnIndex)
                .IsEqualTo(((TurnStartEvent)first[i]).TurnIndex);
        }
    }

    /// <summary>
    ///     Architecture audit v2 §PERF-008 regression test: GetScrollback must
    ///     never block. The previous Channel-based implementation called
    ///     <c>ReadAllAsync().ToBlockingEnumerable()</c> which synchronously
    ///     blocked the calling thread. With the immutable ring buffer, the call
    ///     returns immediately even on an empty bus.
    /// </summary>
    [Test]
    public async Task GetScrollback_OnEmptyBus_ReturnsImmediatelyWithoutBlocking()
    {
        var bus = new InMemoryEventBus(maxScrollback: 4);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        // If GetScrollback blocks, the token will fire before this returns.
        var scrollback = bus.GetScrollback(10);
        await Assert.That(cts.IsCancellationRequested).IsFalse();
        await Assert.That(scrollback.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FailingSubscriber_DoesNotBlockOthers()
    {
        var bus = new InMemoryEventBus();
        var received = new List<AgentEvent>();

        bus.Subscribe(async (evt, ct) => throw new InvalidOperationException("boom"));
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(received.Count).IsEqualTo(1);
    }
}
