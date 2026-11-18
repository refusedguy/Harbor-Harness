// FakeEventBusDrainTests.cs — the drain seam that replaces a fixed sleep (#2705 T04).
//
// A publish is not an instant: the event is recorded, then every subscriber
// runs. A test that sleeps after PublishAsync only hopes the subscribers
// finished. These tests pin the observable fact instead. The bus knows how
// many published events are still being delivered, and WaitForDrainAsync
// returns only after that count reaches zero, including nested publishes.

using Harbor.Abstractions.Events;
using Harbor.TestKit;

namespace Harbor.TestKit.Tests;

public class FakeEventBusDrainTests
{
    [Test]
    public async Task EmptyBus_DrainsImmediately()
    {
        var bus = new FakeEventBus();

        await Assert.That(bus.UnpublishedEventCount).IsEqualTo(0);
        await bus.WaitForDrainAsync(TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task Publish_WithGatedSubscriber_DrainWaitsForDelivery()
    {
        var bus = new FakeEventBus();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<AgentEvent, CancellationToken, ValueTask> handler = async (_, _) => await gate.Task;
        using var subscription = bus.Subscribe(handler);

        Task publish = bus.PublishAsync(TestEvents.AgentStart());
        await Assert.That(bus.UnpublishedEventCount).IsEqualTo(1);

        Task drain = bus.WaitForDrainAsync(TimeSpan.FromSeconds(5));
        await Assert.That(drain.IsCompleted).IsFalse();

        gate.SetResult();
        await drain;
        await publish;

        await Assert.That(bus.UnpublishedEventCount).IsEqualTo(0);
        await Assert.That(bus.Events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NestedPublish_DrainsOnlyAfterNestedSubscriberFinishes()
    {
        var bus = new FakeEventBus();
        var innerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<AgentEvent, CancellationToken, ValueTask> outer = async (e, _) =>
        {
            if (e is AgentStartEvent)
                await bus.PublishAsync(TestEvents.AgentEnd());
        };
        Func<AgentEvent, CancellationToken, ValueTask> inner = async (e, _) =>
        {
            if (e is AgentEndEvent)
            {
                innerStarted.SetResult();
                await innerGate.Task;
            }
        };
        using var outerSubscription = bus.Subscribe(outer);
        using var innerSubscription = bus.Subscribe(inner);

        Task publish = bus.PublishAsync(TestEvents.AgentStart());
        await innerStarted.Task;

        await Assert.That(bus.UnpublishedEventCount).IsEqualTo(2);
        Task drain = bus.WaitForDrainAsync(TimeSpan.FromSeconds(5));
        await Assert.That(drain.IsCompleted).IsFalse();

        innerGate.SetResult();
        await drain;
        await publish;

        await Assert.That(bus.UnpublishedEventCount).IsEqualTo(0);
        await Assert.That(bus.Events.Count).IsEqualTo(2);
    }

    [Test]
    public async Task BlockedDrain_Timeout_NamesPendingEventAndDeadlockShape()
    {
        var bus = new FakeEventBus();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<AgentEvent, CancellationToken, ValueTask> handler = async (_, _) => await gate.Task;
        using var subscription = bus.Subscribe(handler);

        Task publish = bus.PublishAsync(TestEvents.AgentStart());
        var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await bus.WaitForDrainAsync(TimeSpan.FromMilliseconds(30)));

        await Assert.That(error.Message).Contains("AgentStartEvent");
        await Assert.That(error.Message).Contains("WaitForDrainAsync");

        gate.SetResult();
        await publish;
    }

    [Test]
    public async Task BlockedDrain_Cancellation_IsNotReportedAsTimeout()
    {
        var bus = new FakeEventBus();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<AgentEvent, CancellationToken, ValueTask> handler = async (_, _) => await gate.Task;
        using var subscription = bus.Subscribe(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        Task publish = bus.PublishAsync(TestEvents.AgentStart());
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await bus.WaitForDrainAsync(TimeSpan.FromSeconds(5), cts.Token));

        gate.SetResult();
        await publish;
        await Assert.That(bus.UnpublishedEventCount).IsEqualTo(0);
    }
}
