using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     #518 — guard for the invariant <i>"a retention cost that nobody pays for
///     must not be charged"</i>.
/// </summary>
/// <remarks>
///     <para>
///         The bus keeps a fixed-capacity scrollback ring so that history is
///         available as an explicit pull via <see cref="IEventBus.GetScrollback" />
///         (declared contract, <c>docs/EVENT_TOPOLOGY.md</c> §4, #44). Before this
///         guard, the ring's mere <i>capacity</i> decided the fast path: the guard
///         read <c>maxScrollback == 0 &amp;&amp; no mandatory sink</c>, so every
///         shipped preset (<c>EventBusScrollback = 1000</c>) scored 0/200
///         qualifying publishes on CLI and desktop — a retention ring that
///         nothing ever read was the sole reason the most-measured fast path in
///         the codebase was unreachable in production.
///     </para>
///     <para>
///         <b>Why this is a guard and not a cleanup.</b> "A data structure nobody
///         reads" is not statically checkable here, and that is exactly why
///         deleting the ring was rejected: <see cref="IEventBus" /> is public in
///         the zero-dependency <c>Harbor.Abstractions</c> assembly that every
///         plugin references, <c>IPluginLoadHost.EventBus</c> hands a live bus to
///         out-of-tree plugin code, and the desktop app declares
///         <c>[Exposes(typeof(IEventBus))]</c> as a validated DI capability. A
///         plugin can call <c>GetScrollback</c> at any time; no caller census can
///         prove otherwise. So the checkable form of the property is not "no
///         readers exist" but the consequence that was actually wrong: an
///         unobserved ring must cost nothing and must not stand between a
///         composed bus and its fast path.
///     </para>
///     <para>
///         The fix arms retention on the first read rather than at construction,
///         so the cost is paid by whoever asks for history and by nobody else.
///         These cases pin both halves — the ring stays free while unread, and it
///         is genuinely maintained once someone reads it, so the fast path can
///         never be had by silently dropping history.
///     </para>
/// </remarks>
public class EventBusRetentionArmingGuardTests
{
    /// <summary>
    ///     The shipped capacity. The CLI preset sets
    ///     <c>EventBusScrollback = 1000</c> in <c>HostBuilder.CliOptions</c> and
    ///     the desktop preset inherits the library default; 1000 is both, and
    ///     using it here is what makes this guard a regression test for the
    ///     production number rather than for a hand-picked small ring.
    /// </summary>
    private const int ShippedCapacity = 1000;

    /// <summary>
    ///     The guard proper: capacity alone must not disqualify the fast path.
    ///     Zero subscribers and no sinks means nothing can observe a publish that
    ///     is not retained, so the publish must take the fast path — the
    ///     <c>PublishedCount == 0</c> discriminator sits strictly below the
    ///     queue-age envelope, which only the slow path enters.
    /// </summary>
    [Test]
    public async Task ShippedCapacity_ZeroSubscribersNoSinks_TakesFastPath()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, ShippedCapacity);

        await Assert.That(bus.HasMandatorySink).IsFalse();
        await Assert.That(bus.FastPathEligible).IsTrue()
            .Because("a ring that no reader has asked for is a potential, not an obligation (#518)");

        for (int i = 0; i < 200; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        await Assert.That(bus.FastPathCount).IsEqualTo(200);
        await Assert.That(bus.PublishedCount).IsEqualTo(0)
            .Because("the fast path returns before the queue-age envelope; every publish qualified");
        await Assert.That(bus.FastPathCount + bus.PublishedCount).IsEqualTo(200)
            .Because("the total publish count must stay exact, not under-report the skipped publishes");
    }

    /// <summary>
    ///     The ring is not maintained while nobody reads it. Observable through
    ///     the same public surface a reader would use, which is the only place
    ///     this is visible from: the first read sees an empty history, because
    ///     nothing was retained to put in it.
    /// </summary>
    [Test]
    public async Task UnreadRing_RetainsNothing_AndArmsOnTheFirstRead()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, ShippedCapacity);

        for (int i = 0; i < 200; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        // This call is the reader, and it is what arms retention. It returns the
        // history accumulated from arming onward — which, on the first read, is
        // nothing, because the 200 publishes above ran with retention disarmed.
        var first = bus.GetScrollback(ShippedCapacity);
        await Assert.That(first.Count).IsEqualTo(0)
            .Because("history starts at the first read, not at construction (#518)");

        // Arming is one-way: the read above is enough, and it is the ring that
        // now costs, not a second opt-in.
        await Assert.That(bus.FastPathEligible).IsFalse();
    }

    /// <summary>
    ///     ...and the armed ring is a real ring, not a stub. Once a reader has
    ///     appeared every publish is retained again and the fast path is off.
    ///     Without this half, "the fast path is reachable" would be achievable by
    ///     simply dropping history, which is the opposite of the fix.
    /// </summary>
    [Test]
    public async Task ArmedRing_RetainsEveryLaterPublish_AndLeavesTheFastPath()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 4);

        _ = bus.GetScrollback(4); // arm retention

        for (int i = 0; i < 6; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        await Assert.That(bus.PublishedCount).IsEqualTo(6)
            .Because("an armed ring is a retention sink, so every publish is observable and accounted");
        await Assert.That(bus.FastPathCount).IsEqualTo(0);

        // Drop-oldest still holds after arming: capacity 4, six events in, the
        // two oldest are gone. This is the pre-existing eviction guarantee
        // (EventTopologySemanticsTests.Scrollback_EvictsOldest_NoLossBelowCapacity)
        // asserted on the armed path.
        var tail = bus.GetScrollback(10);
        await Assert.That(tail.Count).IsEqualTo(4);
        await Assert.That(((TurnStartEvent)tail[0]).TurnIndex).IsEqualTo(2);
        await Assert.That(((TurnStartEvent)tail[3]).TurnIndex).IsEqualTo(5);
    }

    /// <summary>
    ///     A reader is a reader whatever it asks for: even a zero-length pull
    ///     (a "is there any history?" probe) is evidence that history is consumed,
    ///     and must arm retention rather than leave the bus quietly free. Pinning
    ///     this keeps the fast path from being won by a probe that never reads.
    /// </summary>
    [Test]
    public async Task ZeroLengthRead_StillArmsRetention()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, ShippedCapacity);

        var probe = bus.GetScrollback(0);
        await Assert.That(probe.Count).IsEqualTo(0);
        await Assert.That(bus.FastPathEligible).IsFalse()
            .Because("an armed ring is the price of a reader, and GetScrollback(0) is a reader (#518)");

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(bus.PublishedCount).IsEqualTo(1);
        await Assert.That(bus.GetScrollback(8).Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A disabled ring stays disabled and cannot be armed: with no slots
    ///     there is nothing to maintain, so the fast path must remain available
    ///     no matter how often history is asked for. This is the pre-#518
    ///     behaviour of <c>EventBusScrollback = 0</c> and the reason the headless
    ///     preset measured 200/200 while the shipped ones measured 0/200.
    /// </summary>
    [Test]
    public async Task DisabledRing_StaysFast_EvenAfterARead()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);

        _ = bus.GetScrollback(64);
        await Assert.That(bus.GetScrollback(64).Count).IsEqualTo(0);

        await Assert.That(bus.FastPathEligible).IsTrue()
            .Because("with no slots there is no ring to maintain, so nothing can observe the publish");

        await bus.PublishAsync(new TurnStartEvent(1));
        await Assert.That(bus.PublishedCount).IsEqualTo(0)
            .Because("the fast path is still the whole story for a bus with no ring");
    }
}
