using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
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

        // #518: arm retention — history starts at the first read, so the ring is
        // not maintained for these publishes until somebody asks for it.
        _ = bus.GetScrollback(5);

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

        // #518: arm retention before publishing (see
        // EventBusRetentionArmingGuardTests for the full arming contract).
        _ = bus.GetScrollback(8);

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

    /// <summary>
    ///     #97 per-delta slice regression: moving the PublishAsync Debug log
    ///     below the zero-subscriber fast path (and guarding it with IsEnabled)
    ///     must not change observable behavior. The same 3 per-delta/per-message
    ///     event shapes deliver identically — same order, same instance
    ///     equality, same scrollback tail — as before the reorder.
    /// </summary>
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Test]
    public async Task PublishAsync_PerDeltaShapes_DeliverIdentically(int shape)
    {
        var bus = new InMemoryEventBus(maxScrollback: 8);

        // #518: arm retention so the ring is actually maintained for the publish
        // below — otherwise this subscriber-only bus would take the slow path
        // without ever writing a slot, and the scrollback tail assertion below
        // would be reading an empty ring.
        _ = bus.GetScrollback(8);

        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        AgentEvent evt = shape switch
        {
            0 => new MessageUpdateEvent(
                new TextDeltaEvent("m1", "hello"),
                AssistantMessage.Empty("s", "m")),
            1 => new MessageUpdateEvent(
                new ToolCallDeltaEvent("tc_1", "{\"path\":"),
                AssistantMessage.Empty("s", "m")),
            _ => new TurnStartEvent(7),
        };

        await bus.PublishAsync(evt);

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo(evt);
        var tail = bus.GetScrollback(1);
        await Assert.That(tail.Count).IsEqualTo(1);
        await Assert.That(tail[0]).IsEqualTo(evt);
    }

    /// <summary>
    ///     #47 queue-age instrumentation: the counters track slow-path publishes
    ///     and reset to zero when quiescent, without changing delivery.
    /// </summary>
    [Test]
    public async Task PublishAsync_QueueAgeInstrumentation_TracksAndResets()
    {
        var bus = new InMemoryEventBus(maxScrollback: 8);
        await Assert.That(bus.PublishedCount).IsEqualTo(0);
        await Assert.That(bus.InflightPublishCount).IsEqualTo(0);
        await Assert.That(bus.OldestPendingAge).IsEqualTo(TimeSpan.Zero);

        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        var evt = new TurnStartEvent(1);
        await bus.PublishAsync(evt);

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo(evt);
        await Assert.That(bus.PublishedCount).IsEqualTo(1);
        await Assert.That(bus.InflightPublishCount).IsEqualTo(0);
        await Assert.That(bus.OldestPendingAge).IsEqualTo(TimeSpan.Zero);
        await Assert.That(bus.MaxDispatchDuration.Ticks).IsGreaterThanOrEqualTo(0);
    }

    /// <summary>
    ///     #47 queue-age instrumentation: while a publish is blocked inside a
    ///     subscriber, InflightPublishCount is 1 and OldestPendingAge grows;
    ///     both reset once the publish completes.
    /// </summary>
    [Test]
    public async Task PublishAsync_InflightPublish_ExposesOldestPendingAge()
    {
        // Budget disabled so the gated handler below is awaited, not left behind.
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance, maxScrollback: 8, handlerBudget: TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe(async (evt, ct) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });

        Task publish = bus.PublishAsync(new TurnStartEvent(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20);

        await Assert.That(bus.PublishedCount).IsEqualTo(1);
        await Assert.That(bus.InflightPublishCount).IsEqualTo(1);
        await Assert.That(bus.OldestPendingAge.Ticks).IsGreaterThan(0);

        release.TrySetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(bus.InflightPublishCount).IsEqualTo(0);
        await Assert.That(bus.OldestPendingAge).IsEqualTo(TimeSpan.Zero);
    }
}
