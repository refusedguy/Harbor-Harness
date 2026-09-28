using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     #47/S2: the dispatch-duration distribution the telemetry layer exports.
///     A monotonic max cannot separate "one slow outlier" from "everything is
///     slow", so the bus keeps a bounded window of completed publishes and
///     answers p50/p95/p99 over it. These tests pin the semantics (percentile
///     ordering, drain reset, fast-path exclusion, O(1) retention); the exporter
///     itself is covered in tests/Harbor.Telemetry.Tests.
/// </summary>
public class EventBusQueueAgePercentileTests
{
    /// <summary>Hang guard for every await on a gated publish (per the #152 test style).</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>Number of instant publishes in the scripted burst.</summary>
    private const int BurstFastCount = 190;

    /// <summary>
    ///     Number of graded slow publishes at the tail of the burst. Their delays
    ///     ramp 10 ms, 20 ms … 100 ms, so the window's top three samples sit far
    ///     apart: p50 lands in the instant bulk, p99 in the ramp, max at the very
    ///     end. The gaps (tens of ms) dominate timer jitter.
    /// </summary>
    private const int BurstSlowCount = 10;

    /// <summary>
    ///     A scripted burst against a gated handler must produce a real
    ///     distribution: p99 above p50, and the exact max above p99. Also covers
    ///     the oldest-pending gauge — non-zero while a publish is stuck behind
    ///     the gate, back to zero once the queue drains.
    /// </summary>
    [Test]
    public async Task PublishAsync_ScriptedBurst_P99AboveP50_AndMaxAboveP99()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance, maxScrollback: 8, handlerBudget: TimeSpan.Zero);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Script: publish #0 parks on the gate, the next 10 ramp, the rest return
        // immediately. The parked publish is both the oldest-pending proof and
        // (once released) the slowest sample in the window.
        bus.Subscribe(async (evt, ct) =>
        {
            int turn = (evt as TurnStartEvent)?.TurnIndex ?? 0;
            switch (turn)
            {
                case 0:
                    gateEntered.TrySetResult();
                    await gate.Task.ConfigureAwait(false);
                    break;
                case > 0 and <= BurstSlowCount:
                    await Task.Delay(TimeSpan.FromMilliseconds(10 * turn), ct).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        });

        // Publish #0 without awaiting it: it parks inside the handler.
        Task parked = bus.PublishAsync(new TurnStartEvent(0));
        await gateEntered.Task.WaitAsync(HangGuard);
        await Task.Delay(20);

        await Assert.That(bus.InflightPublishCount).IsEqualTo(1);
        await Assert.That(bus.OldestPendingAge.Ticks).IsGreaterThan(0);

        // The rest of the burst runs while #0 is still parked.
        for (int turn = 1; turn <= BurstSlowCount; turn++)
        {
            await bus.PublishAsync(new TurnStartEvent(turn)).WaitAsync(HangGuard);
        }

        for (int i = 0; i < BurstFastCount; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(BurstSlowCount + 1 + i)).WaitAsync(HangGuard);
        }

        gate.TrySetResult();
        await parked.WaitAsync(HangGuard);

        // Drain: the gauge returns to zero and the envelope is complete.
        await Assert.That(bus.InflightPublishCount).IsEqualTo(0);
        await Assert.That(bus.OldestPendingAge).IsEqualTo(TimeSpan.Zero);

        long p50 = bus.DispatchDurationPercentile(0.50).Ticks;
        long p99 = bus.DispatchDurationPercentile(0.99).Ticks;
        long max = bus.MaxDispatchDuration.Ticks;

        // The distribution, not just the outlier: the tail is genuinely slower
        // than the bulk, and the exact max is genuinely above p99.
        await Assert.That(p99).IsGreaterThan(p50);
        await Assert.That(max).IsGreaterThan(p99);

        // The bulk is fast while p99 sits deep in the ramp — a graded
        // distribution, not one slow event among equals.
        await Assert.That(p50).IsLessThan(TimeSpan.FromMilliseconds(100).Ticks);
        await Assert.That(p99).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(10).Ticks);
    }

    /// <summary>
    ///     The percentile window is bounded: retained samples saturate at the
    ///     fixed capacity no matter how many publishes run, so per-bus retained
    ///     memory is O(1) — asserted as such, not merely documented.
    /// </summary>
    [Test]
    public async Task DispatchSampleWindow_SaturatesAtFixedCapacity_O1Retained()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance, maxScrollback: 4, handlerBudget: TimeSpan.Zero);
        bus.Subscribe(static (_, _) => ValueTask.CompletedTask);

        await Assert.That(bus.DispatchSampleCapacity).IsEqualTo(InMemoryEventBus.DispatchSampleWindowCapacity);
        await Assert.That(bus.DispatchSampleCount).IsEqualTo(0);

        for (int i = 0; i < InMemoryEventBus.DispatchSampleWindowCapacity; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
            await Assert.That(bus.DispatchSampleCount).IsEqualTo(i + 1);
        }

        // 20x the capacity — the count must not move.
        for (int i = 0; i < InMemoryEventBus.DispatchSampleWindowCapacity * 20; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        await Assert.That(bus.DispatchSampleCount).IsEqualTo(InMemoryEventBus.DispatchSampleWindowCapacity);
        await Assert.That(bus.PublishedCount).IsEqualTo(InMemoryEventBus.DispatchSampleWindowCapacity * 21);
    }

    /// <summary>
    ///     The zero-subscriber / zero-scrollback fast path returns before the
    ///     envelope, so it never enters the percentile window — otherwise every
    ///     fast publish would drag the distribution towards zero and hide the
    ///     slow ones behind it.
    /// </summary>
    [Test]
    public async Task FastPath_NeverEntersThePercentileWindow()
    {
        var bus = new InMemoryEventBus(maxScrollback: 0);

        for (int i = 0; i < 100; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        await Assert.That(bus.PublishedCount).IsEqualTo(0);
        await Assert.That(bus.DispatchSampleCount).IsEqualTo(0);
        await Assert.That(bus.DispatchDurationPercentile(0.99)).IsEqualTo(TimeSpan.Zero);
    }

    /// <summary>
    ///     An empty window reads as zero, and out-of-range quantiles clamp to the
    ///     window edges instead of throwing — the exporter polls them, and a poll
    ///     must never be the thing that breaks the host.
    /// </summary>
    [Test]
    public async Task DispatchDurationPercentile_EmptyWindowAndClampedQuantiles()
    {
        var bus = new InMemoryEventBus(maxScrollback: 4);
        await Assert.That(bus.DispatchDurationPercentile(0.5)).IsEqualTo(TimeSpan.Zero);

        bus.Subscribe(static (_, _) => ValueTask.CompletedTask);
        await bus.PublishAsync(new TurnStartEvent(1));
        await Task.Delay(15);
        await bus.PublishAsync(new TurnStartEvent(2));

        long slowest = bus.MaxDispatchDuration.Ticks;
        await Assert.That(slowest).IsGreaterThan(0);
        await Assert.That(bus.DispatchDurationPercentile(1.0).Ticks).IsEqualTo(slowest);
        await Assert.That(bus.DispatchDurationPercentile(5.0).Ticks).IsEqualTo(slowest);
        await Assert.That(bus.DispatchDurationPercentile(-1.0).Ticks).IsLessThanOrEqualTo(slowest);
        await Assert.That(bus.DispatchDurationPercentile(double.NaN).Ticks).IsLessThanOrEqualTo(slowest);
    }
}
