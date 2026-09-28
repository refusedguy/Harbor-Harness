using Harbor.Abstractions.Events;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Structural proof of which code path a given bus configuration takes
///     through <see cref="InMemoryEventBus.PublishAsync" /> (#391).
/// </summary>
/// <remarks>
///     The fast path is an early return guarded by the composition-time constant
///     <c>_fastPathEligible</c> (<c>maxScrollback == 0 &amp;&amp; no mandatory sink</c>)
///     plus a lock-free read of <c>_subscriptions.IsEmpty</c>; the no-middleware
///     case then hands back the cached <c>Task.CompletedTask</c> instead of entering
///     <c>DrainOptionalSinksAsync</c> (src/Harbor.Registries/Events/InMemoryEventBus.cs).
///     Nothing observable distinguishes it from the slow path except that it never
///     reaches the <c>#47</c> queue-age envelope, whose <c>PublishedCount</c>
///     increment sits strictly below the return. That gives an exact, allocation-free
///     discriminator — and it is what makes the two <c>*_ScrollbackOff</c> /
///     <c>*_ScrollbackOn</c> rows of <c>tests/Harbor.Benchmarks/EventBusBenchmark.cs</c>
///     different measurements rather than the same one measured twice.
///     <para>
///         <c>PublishAsync_ZeroSubscribers_IsAllocationFree</c> in
///         <c>AllocationBudgetTests.cs</c> pins the 0-allocation property of the fast
///         path; these cases pin <i>reachability</i>, which the benchmark cannot assert
///         on its own and which no other test covered.
///     </para>
/// </remarks>
public class EventBusFastPathTests
{
    /// <summary>
    ///     A bus that satisfies the fast-path guard returns before the queue-age
    ///     envelope, so <see cref="InMemoryEventBus.PublishedCount" /> stays at zero
    ///     however many events are published — and the returned task is already
    ///     complete, because nothing on the path suspends.
    /// </summary>
    [Test]
    public async Task PublishAsync_ZeroSubscribersNoScrollback_TakesFastPath()
    {
        var bus = new InMemoryEventBus(maxScrollback: 0);
        var evt = new TurnStartEvent(1);

        for (int i = 0; i < 3; i++)
        {
            Task published = bus.PublishAsync(evt);
            await Assert.That(published.IsCompletedSuccessfully).IsTrue();
            await published;
        }

        await Assert.That(bus.PublishedCount).IsEqualTo(0);
        await Assert.That(bus.InflightPublishCount).IsEqualTo(0);
    }

    /// <summary>
    ///     Counterpart to <see cref="PublishAsync_ZeroSubscribersNoScrollback_TakesFastPath" />:
    ///     enabling scrollback alone is enough to leave the fast path, so the
    ///     scrollback-on rows of the benchmark measure the slow path (ring append +
    ///     middleware pipeline) and not a zero-alloc early return.
    /// </summary>
    [Test]
    public async Task PublishAsync_ZeroSubscribersWithScrollback_LeavesFastPath()
    {
        var bus = new InMemoryEventBus(maxScrollback: 8);
        var evt = new TurnStartEvent(1);

        await bus.PublishAsync(evt);

        await Assert.That(bus.PublishedCount).IsEqualTo(1);
        await Assert.That(bus.InflightPublishCount).IsEqualTo(0);
    }

    /// <summary>
    ///     A single subscriber also leaves the fast path — the guard requires an
    ///     empty subscription set, so the fan-out rows are slow-path rows regardless
    ///     of their scrollback setting.
    /// </summary>
    [Test]
    public async Task PublishAsync_OneSubscriberNoScrollback_LeavesFastPath()
    {
        var bus = new InMemoryEventBus(maxScrollback: 0);
        bus.Subscribe(static (_, _) => ValueTask.CompletedTask);

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(bus.PublishedCount).IsEqualTo(1);
    }
}
