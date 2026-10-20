using Harbor.Abstractions.Events;
using TUnit.Assertions;
using Harbor.Registries.Events;
using Harbor.TestKit;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #1108 regression tests: the subscription snapshot on the publish
///     path is read with acquire semantics (<c>Volatile.Read</c>), so a
///     handler that just subscribed can never be skipped because the
///     publisher observed a stale array — a real shape on ARM's weak memory
///     model, where a reference-sized (torn-free) plain read may still lag
///     the <c>ImmutableInterlocked</c> write that published it.
/// </summary>
public class EventBusSubscriptionVisibilityTests
{
    /// <summary>
    ///     Rapid subscribe → publish → unsubscribe churn must deliver every
    ///     event to the live subscriber: the contract the fenced snapshot
    ///     read protects.
    /// </summary>
    [Test]
    public async Task PublishAsync_SubscriberChurn_NeverSkipsAnEvent()
    {
        var bus = new InMemoryEventBus(maxScrollback: 0);

        for (int i = 0; i < 200; i++)
        {
            int seen = 0;
            using (bus.Subscribe((_, _) =>
                   {
                       Interlocked.Increment(ref seen);
                       return ValueTask.CompletedTask;
                   }))
            {
                await bus.PublishAsync(TestEvents.TurnStart(i));
            }

            await Assert.That(seen).IsEqualTo(1)
                .Because($"publish #{i} had exactly one live subscriber, which must observe it");
        }
    }

    /// <summary>
    ///     A subscriber added while another publish is in flight is either in
    ///     the snapshot (and sees the event) or not (and sees the next one) —
    ///     but once <c>Subscribe</c> has returned, the following publish must
    ///     observe it: the write fence in <c>Subscribe</c> pairs with the
    ///     acquire read on the publish path.
    /// </summary>
    [Test]
    public async Task PublishAsync_SubscribeReturned_PublishObservesIt()
    {
        var bus = new InMemoryEventBus(maxScrollback: 0);
        int seen = 0;
        using (bus.Subscribe((_, _) =>
               {
                   Interlocked.Increment(ref seen);
                   return ValueTask.CompletedTask;
               }))
        {
            await bus.PublishAsync(TestEvents.TurnStart());
            await bus.PublishAsync(TestEvents.TurnStart(2));
        }

        await Assert.That(seen).IsEqualTo(2);
    }
}
