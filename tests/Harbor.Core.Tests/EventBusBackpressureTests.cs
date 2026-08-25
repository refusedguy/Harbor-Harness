using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     A4 backpressure on the bus (sprint 6): a slow subscriber cannot stall
///     the publisher beyond its per-handler budget; consecutive strikes evict
///     it; fast subscribers keep the publish-then-observe contract.
/// </summary>
public class EventBusBackpressureTests
{
    private static InMemoryEventBus BusWithBudget(TimeSpan budget) => new(
        NullLogger<InMemoryEventBus>.Instance, maxScrollback: 0, handlerBudget: budget);

    [Test]
    public async Task FastSubscriber_ReceivesEventSynchronously()
    {
        var bus = BusWithBudget(TimeSpan.FromMilliseconds(250));
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) =>
        {
            received.Add(evt);
            await Task.CompletedTask;
        });

        var evt = new TurnStartEvent(1);
        await bus.PublishAsync(evt);

        await Assert.That(received.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SlowSubscriber_DoesNotStallPublisher_AndIsEvictedAfterStrikes()
    {
        var bus = BusWithBudget(TimeSpan.FromMilliseconds(50));
        int deliveries = 0;
        ValueTask SlowHandler(AgentEvent evt, CancellationToken ct)
        {
            Interlocked.Increment(ref deliveries);
            return new ValueTask(Task.Delay(TimeSpan.FromSeconds(30), ct));
        }

        IDisposable sub = bus.Subscribe(SlowHandler);

        // Three strikes → eviction. Each publish must return promptly
        // instead of waiting out the 30-second handler.
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await bus.PublishAsync(new TurnStartEvent(i));
            sw.Stop();
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(2_000);
        }

        // Give the last orphaned handler's bookkeeping a beat.
        await Task.Delay(100);
        _ = sub; // subscription identity kept alive for the assertion below

        // After eviction the slow subscriber no longer receives anything —
        // and publishes stay fast.
        var fourth = System.Diagnostics.Stopwatch.StartNew();
        await bus.PublishAsync(new TurnStartEvent(99));
        fourth.Stop();
        await Assert.That(fourth.ElapsedMilliseconds).IsLessThan(500);

        await Assert.That(Volatile.Read(ref deliveries)).IsEqualTo(3);
    }

    [Test]
    public async Task DisabledBudget_KeepsLegacyBlockingSemantics()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance, 0, handlerBudget: TimeSpan.Zero);
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) =>
        {
            await Task.Delay(120, ct);
            received.Add(evt);
        });

        await bus.PublishAsync(new TurnStartEvent(1));

        // No budget ⇒ publisher waited out the 120ms handler.
        await Assert.That(received.Count).IsEqualTo(1);
    }
}
