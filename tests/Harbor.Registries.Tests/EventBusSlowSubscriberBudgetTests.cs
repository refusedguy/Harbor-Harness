using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using Harbor.Registries.Events;
using Harbor.TestKit;

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
    [Timeout(30_000)]
    public async Task PublishAsync_SyncCancellingSubscriberWithBudget_StrikesThenEvicts(CancellationToken cancellationToken)
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            maxScrollback: 0,
            handlerBudget: TimeSpan.FromMilliseconds(50));
        int calls = 0;
        bus.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref calls);
            // An already-canceled ValueTask, NOT a synchronous throw: a throw
            // at invocation time never becomes a dispatch — it escapes to the
            // fan-out's catch and evicts immediately. Only a completed
            // (faulted/canceled) dispatch travels the shortcut below.
            return new ValueTask(Task.FromCanceled(new CancellationToken(canceled: true)));
        });

        for (int i = 0; i < 4; i++)
        {
            await bus.PublishAsync(TestEvents.TurnStart(i), cancellationToken);
        }

        await Assert.That(calls).IsEqualTo(3)
            .Because("3 slow strikes evict the subscriber, so the 4th publish must not reach it");
    }

    /// <summary>
    ///     #2562 regression: with parallel publishes, a fast dispatch from an
    ///     earlier publish can have its outcome recorded AFTER two later slow
    ///     strikes. The counter must still see that fast dispatch between them
    ///     and keep the subscriber. The old single shared int let the slow
    ///     strikes pile onto the first one and evict a healthy subscriber.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task PublishAsync_LateFastOutcomeBetweenSlowStrikes_DoesNotEvictSubscriber()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            maxScrollback: 0,
            handlerBudget: TimeSpan.FromMinutes(1));
        var fastGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        bus.Subscribe((evt, _) =>
        {
            Interlocked.Increment(ref calls);
            if (((TurnStartEvent)evt).TurnIndex == 2)
            {
                // The fast dispatch. Its Completed outcome is recorded only
                // after the gate is released, i.e. after the two slow strikes.
                return AwaitGateAsync(fastGate.Task);
            }

            // An already-canceled dispatch takes the completed-handler shortcut
            // and becomes a Slow strike immediately, without the budget timer.
            return new ValueTask(Task.FromCanceled(new CancellationToken(canceled: true)));
        });

        static async ValueTask AwaitGateAsync(Task gate) => await gate.ConfigureAwait(false);

        // Serial 1: slow (streak 1).
        await bus.PublishAsync(TestEvents.TurnStart(1));

        // Serial 2: fast, but held open so its outcome lands last.
        Task gatedPublish = bus.PublishAsync(TestEvents.TurnStart(2));

        // Serials 3 and 4: slow. With a shared counter these three slow outcomes
        // reached the eviction threshold even though serial 2 was fast.
        await bus.PublishAsync(TestEvents.TurnStart(3));
        await bus.PublishAsync(TestEvents.TurnStart(4));

        fastGate.SetResult();
        await gatedPublish;

        // The subscriber is still attached: a further publish reaches it.
        int before = Volatile.Read(ref calls);
        await bus.PublishAsync(TestEvents.TurnStart(5));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(before + 1)
            .Because("a fast dispatch between slow strikes breaks the streak");
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
        await bus.PublishAsync(TestEvents.TurnStart(), cts.Token);

        await Assert.That(calls).IsEqualTo(1);
    }
}
