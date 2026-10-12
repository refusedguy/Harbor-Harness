using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using Harbor.Registries.Events;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #1108 regression tests: the cancellable slow-subscriber race
///     (<c>WhenAny(handler, Delay(Infinite, ct))</c>) is skipped when the
///     handler has already finished — awaiting it directly is exactly
///     equivalent (the infinite delay fed by a live token could only lose)
///     and saves the Timer + Task the delay allocates per dispatch. Only the
///     non-cancellable half had this shortcut (#47); the cancellable half
///     paid it for every synchronously faulted/cancelled handler.
/// </summary>
public class EventBusSlowSubscriberBudgetTests
{
    /// <summary>
    ///     A handler that synchronously observes budget cancellation earns one
    ///     slow strike per publish through the completed-handler shortcut and
    ///     is evicted after <c>MaxSlowStrikes</c> — the same strike/evict
    ///     contract as the <c>WhenAny</c> race it bypasses.
    /// </summary>
    [Test]
    public async Task PublishAsync_SyncCancellingSubscriberWithBudget_StrikesThenEvicts()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            maxScrollback: 0,
            handlerBudget: TimeSpan.FromMilliseconds(50));
        int calls = 0;
        bus.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new OperationCanceledException("budget probe");
        });

        using var cts = new CancellationTokenSource();
        for (int i = 0; i < 4; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i), cts.Token);
        }

        await Assert.That(calls).IsEqualTo(3)
            .Because("3 slow strikes evict the subscriber, so the 4th publish must not reach it");
    }

    /// <summary>
    ///     The shortcut must not swallow a publisher teardown: with the outer
    ///     token already cancelled, a finished handler still resolves without
    ///     hanging the publisher.
    /// </summary>
    [Test]
    public async Task PublishAsync_CompletedHandlerWithCancelledPublisher_StillResolves()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            maxScrollback: 0,
            handlerBudget: TimeSpan.FromMilliseconds(50));
        int calls = 0;
        bus.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await bus.PublishAsync(new TurnStartEvent(1), cts.Token);

        await Assert.That(calls).IsEqualTo(1);
    }
}
