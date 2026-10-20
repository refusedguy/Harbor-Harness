using Harbor.Abstractions.Events;
using Harbor.Registries.Events;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     #47/S3 — the zero-subscriber fast path is now decided by an enumerated
///     mandatory/optional verdict per sink (<c>docs/EVENT_BUS_SINKS.md</c>)
///     rather than by "no middleware at all". These tests pin the three
///     semantic cases the slice is defined by, plus the promise that nothing is
///     lost silently: optional sinks are drained and counted, and the total
///     publish count never under-reports.
/// </summary>
public class EventBusSinkVerdictTests
{
    /// <summary>Warmup publishes, published before the allocation measurement.</summary>
    private const int Warmup = 3_000;

    // ── (a) a mandatory sink keeps the event ────────────────────────────────

    /// <summary>
    ///     (a) 0 subscribers + a MANDATORY sink → the fast path must not be
    ///     taken and the event must still reach the sink. A filter is not a
    ///     listener: dropping the event for it would push unapproved event
    ///     types into the projections downstream.
    /// </summary>
    [Test]
    public async Task MandatorySink_ZeroSubscribers_StillReachesTheSink()
    {
        var sink = new RecordingEventBusMiddleware("recording", EventBusSinkKind.Mandatory);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0, new[] { sink });

        await Assert.That(bus.HasMandatorySink).IsTrue()
            .Because("a sink that declared EventBusSinkKind.Mandatory must be visible in the decision");
        await Assert.That(bus.FastPathEligible).IsFalse();

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(sink.Seen).IsEqualTo(1);
        await Assert.That(bus.FastPathCount).IsEqualTo(0)
            .Because("a mandatory sink rules the fast path out — nothing may be skipped for it");
        await Assert.That(bus.PublishedCount).IsEqualTo(1);
    }

    /// <summary>
    ///     A sink that never declared a verdict inherits
    ///     <see cref="EventBusSinkKind.Mandatory" /> from the interface default,
    ///     so a middleware author who has not thought about the question cannot
    ///     be bypassed by accident.
    /// </summary>
    [Test]
    public async Task SinkWithoutAVerdict_IsTreatedAsMandatory()
    {
        IEventBusMiddleware sink = new UndeclaredSinkEventBusMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0, new[] { sink });

        await Assert.That(sink.SinkKind).IsEqualTo(EventBusSinkKind.Mandatory)
            .Because("the interface default is the conservative direction");
        await Assert.That(bus.HasMandatorySink).IsTrue();

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(bus.FastPathCount).IsEqualTo(0);
    }

    /// <summary>
    ///     An ARMED scrollback ring is a retention sink: with slots to write, the
    ///     bus is not eligible even without a single mandatory sink. #518 moved
    ///     the arming from capacity to the first reader, so the bus is asked for
    ///     history first — that call is what turns a potential into an
    ///     obligation, and from there the ring behaves exactly as before.
    /// </summary>
    [Test]
    public async Task ArmedScrollback_DisablesTheFastPath()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 8);

        // Arm retention (#518). A bus with slots but no reader is eligible —
        // see EventBusRetentionArmingGuardTests.
        _ = bus.GetScrollback(8);

        await Assert.That(bus.HasMandatorySink).IsFalse();
        await Assert.That(bus.FastPathEligible).IsFalse();

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(bus.GetScrollback(8).Count).IsEqualTo(1);
        await Assert.That(bus.FastPathCount).IsEqualTo(0);
    }

    // ── (b) qualifying composition: completed task, zero allocation ─────────

    /// <summary>
    ///     (b) 0 subscribers + no mandatory sink → the returned task is already
    ///     completed and the publish allocates nothing.
    /// </summary>
    [Test]
    public async Task QualifyingComposition_ReturnsCompletedTask_AndAllocatesNothing()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);

        await Assert.That(bus.FastPathEligible).IsTrue();
        await Assert.That(bus.PublishAsync(TestEvents.TurnStart()).IsCompletedSuccessfully).IsTrue()
            .Because("the fast path returns the cached completed task — no state machine, no Task allocation");

        var evt = TestEvents.TurnStart();
        for (int i = 0; i < Warmup; i++)
        {
            await bus.PublishAsync(evt);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int publishes = 5_000;
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"eventbus-fastpath: {publishes} qualifying publishes = {allocated} B ({(double)allocated / publishes:F2} B/publish)");
        await Assert.That(allocated).IsEqualTo(0)
            .Because("the qualifying fast path is the #47/S3 zero-allocation claim; a regression here is a regression in the claim");
        await Assert.That(bus.FastPathCount).IsEqualTo(1 + Warmup + publishes)
            .Because("the probe publish above is a fast-path publish too — nothing on this path may be uncounted");
    }

    // ── (c) one subscriber rules the fast path out ──────────────────────────

    /// <summary>
    ///     (c) 1 subscriber → the fast path is NOT taken even with zero
    ///     middleware and zero scrollback, and the handler observes the event.
    /// </summary>
    [Test]
    public async Task OneSubscriber_DisablesTheFastPath_AndStillDelivers()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);
        int delivered = 0;
        bus.Subscribe((_, _) =>
        {
            delivered++;
            return ValueTask.CompletedTask;
        });

        var evt = TestEvents.TurnStart();
        await bus.PublishAsync(evt);

        await Assert.That(delivered).IsEqualTo(1);
        await Assert.That(bus.FastPathCount).IsEqualTo(0)
            .Because("a live subscriber is the whole reason the fast path exists; skipping it would lose the event");
        await Assert.That(bus.PublishedCount).IsEqualTo(1);
    }

    // ── optional sinks: drained and counted, never skipped ─────────────────

    /// <summary>
    ///     An optional sink registered on an otherwise-qualifying bus is
    ///     DRAINED, not skipped: the sampler still sees the event, the drain is
    ///     counted, and the publish still allocates nothing (the drain awaits
    ///     only already-completed <see cref="ValueTask" />s).
    /// </summary>
    [Test]
    public async Task OptionalSink_IsDrainedOnTheFastPath_NotSkipped()
    {
        var sink = new RecordingEventBusMiddleware("recording", EventBusSinkKind.Optional);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0, new[] { sink });

        await Assert.That(bus.HasMandatorySink).IsFalse();
        await Assert.That(bus.FastPathEligible).IsTrue()
            .Because("only OPTIONAL sinks are attached — that is the case the old guard could not express");

        var evt = TestEvents.TurnStart();
        await bus.PublishAsync(evt);

        await Assert.That(sink.Seen).IsEqualTo(1)
            .Because("optional means 'nothing downstream breaks', not 'may be skipped in silence'");
        await Assert.That(bus.FastPathCount).IsEqualTo(1);
        await Assert.That(bus.OptionalSinkDrainCount).IsEqualTo(1);

        for (int i = 0; i < Warmup; i++)
        {
            await bus.PublishAsync(evt);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int publishes = 5_000;
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"eventbus-fastpath: {publishes} optional-drain publishes = {allocated} B ({(double)allocated / publishes:F2} B/publish)");
        await Assert.That(allocated).IsEqualTo(0)
            .Because("an indexed drain over synchronous sinks must not reintroduce an allocation");
        await Assert.That(sink.Seen).IsEqualTo(1 + Warmup + publishes)
            .Because("every publish on this bus must reach the optional sink — the probe one included");
    }

    /// <summary>
    ///     A sampler that drops an event on the fast path is counted — the drop
    ///     is the sink's own decision, and the bus never hides one.
    /// </summary>
    [Test]
    public async Task OptionalSinkDrop_IsCounted()
    {
        var sink = new DropEventBusMiddleware("dropping-optional", EventBusSinkKind.Optional);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0, new[] { sink });

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(bus.OptionalSinkDropCount).IsEqualTo(1)
            .Because("silent loss is not an option — a drop has to be countable");
        await Assert.That(bus.OptionalSinkDrainCount).IsEqualTo(1);
    }

    /// <summary>
    ///     The builtin sampler's verdict is the one the enumeration table
    ///     records: a throttle whose own job is to drop events is optional, so a
    ///     bus whose only sink is a sampler qualifies for the fast path.
    /// </summary>
    [Test]
    public async Task SamplingMiddleware_IsOptional_AndStaysDrained()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            0,
            new[] { new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 1.0) });

        await Assert.That(bus.HasMandatorySink).IsFalse()
            .Because("SamplingMiddleware declares EventBusSinkKind.Optional (docs/EVENT_BUS_SINKS.md)");

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(bus.FastPathCount).IsEqualTo(1);
        await Assert.That(bus.OptionalSinkDrainCount).IsEqualTo(1);
        await Assert.That(bus.OptionalSinkDropCount).IsEqualTo(0);
    }

    /// <summary>
    ///     An optional sink that completes asynchronously is awaited rather than
    ///     abandoned: the returned task is not yet complete and the sink has run
    ///     by the time it finishes.
    /// </summary>
    [Test]
    public async Task AsyncOptionalSink_IsAwaited_NotAbandoned()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new GatedEventBusMiddleware(gate.Task, "gated-optional", EventBusSinkKind.Optional);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0, new[] { sink });

        Task publish = bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(publish.IsCompleted).IsFalse()
            .Because("an incomplete ValueTask from an optional sink must not be fire-and-forgotten");
        await Assert.That(bus.FastPathCount).IsEqualTo(1);

        gate.TrySetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(sink.Seen).IsEqualTo(1);
    }

    // ── publish accounting is never lossy ─────────────────────────────────

    /// <summary>
    ///     The fast path skips the queue-age envelope, so
    ///     <c>FastPathCount + PublishedCount</c> — not either counter alone — is
    ///     the total publish count. This is what makes the "what fraction of
    ///     publishes qualify" question answerable at runtime instead of
    ///     estimated.
    /// </summary>
    [Test]
    public async Task TotalPublishCount_AccountsForBothPaths()
    {
        var qualifying = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);
        var retained = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 4);
        var withSubscriber = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 0);
        withSubscriber.Subscribe(static (_, _) => ValueTask.CompletedTask);

        const int publishes = 100;
        for (int i = 0; i < publishes; i++)
        {
            var evt = TestEvents.TurnStart(i);
            await qualifying.PublishAsync(evt);
            await retained.PublishAsync(evt);
            await withSubscriber.PublishAsync(evt);
        }

        long qualifyingTotal = qualifying.FastPathCount + qualifying.PublishedCount;
        long retainedTotal = retained.FastPathCount + retained.PublishedCount;
        long subscribedTotal = withSubscriber.FastPathCount + withSubscriber.PublishedCount;

        double qualifyingFraction = (double)qualifying.FastPathCount / qualifyingTotal;
        Console.WriteLine(
            $"eventbus-fastpath-fraction: qualifying={qualifyingFraction:P2} "
            + $"scrollback={retained.FastPathCount / (double)retainedTotal:P2} "
            + $"subscriber={withSubscriber.FastPathCount / (double)subscribedTotal:P2}");

        await Assert.That(qualifyingTotal).IsEqualTo(publishes);
        await Assert.That(retainedTotal).IsEqualTo(publishes);
        await Assert.That(subscribedTotal).IsEqualTo(publishes);
        await Assert.That(qualifyingFraction).IsEqualTo(1.0);
    }
}
