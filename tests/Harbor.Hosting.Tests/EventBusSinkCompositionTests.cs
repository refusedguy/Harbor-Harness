// EventBusSinkCompositionTests.cs — #47/S3.
//
// The fast path is only meaningful if somebody can say, honestly, how often a
// real publish takes it. The composition root is where the answer is decided
// (scrollback capacity + the mandatory/optional verdict of each registered
// sink), so the fraction is measured HERE, against the same AddHarbor call the
// CLI, the desktop apps and the embedders make — not against a hand-built bus.
//
// What this pins:
//   * the shipped presets are measured, not estimated (printed per row);
//   * a mandatory sink keeps the bus off the fast path, whatever the capacity;
//   * scrollback capacity alone does NOT disqualify a bus — an unread ring is a
//     potential, not an obligation (#518). Combined with #478, which left the
//     shipped presets with no sinks at all, that is why BOTH the CLI and the
//     desktop rows below now qualify in full;
//   * FastPathCount + PublishedCount is the total publish count in every case,
//     so the fast path never hides publishes from the queue-age envelope.

using Harbor.Abstractions.Events;
using Harbor.Hosting;
using Harbor.Registries.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Measures the fast-path qualification fraction of the shipped composition
///     presets through the real <c>AddHarbor</c> composition root (#47/S3).
///     Full verdict table: <c>docs/EVENT_BUS_SINKS.md</c>.
/// </summary>
[NotInParallel("hosting")]
public class EventBusSinkCompositionTests
{
    private const int Publishes = 200;

    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-sink-composition-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Compose through the real root and publish <see cref="Publishes" /> events.</summary>
    private static async Task<(long Fast, long Slow, InMemoryEventBus Bus)> MeasureAsync(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        using ServiceProvider sp = services.BuildServiceProvider();

        var bus = (InMemoryEventBus)sp.GetRequiredService<IEventBus>();

        for (int i = 0; i < Publishes; i++)
        {
            await bus.PublishAsync(new TurnStartEvent(i));
        }

        return (bus.FastPathCount, bus.PublishedCount, bus);
    }

    /// <summary>
    ///     The CLI preset exactly as <c>HostBuilder.CliOptions</c> builds it:
    ///     1000-slot scrollback and <b>no middleware</b> (#478 removed the
    ///     typeless <c>TypeFilterMiddleware</c> that used to sit here).
    /// </summary>
    /// <remarks>
    ///     This row is where #478 and #518 meet. #478 took away the preset's
    ///     mandatory-sink blocker — the filter was mandatory in name only,
    ///     admitting every event it was supposed to police — which left the
    ///     1000-slot scrollback as the <em>only</em> reason the CLI bus could not
    ///     take the fast path. #518 then removed that reason too: capacity alone
    ///     is a potential, not an obligation, and an unread ring is not
    ///     maintained. So the CLI preset, which measured 0/200 under either
    ///     change on its own, now measures 200/200 under both. It is structurally
    ///     the desktop preset now — no sinks, unread retention — and the two rows
    ///     below are asserted separately precisely so a future re-divergence is
    ///     visible rather than averaged away.
    /// </remarks>
    [Test]
    public async Task CliPreset_ZeroSubscribers_Qualifies()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            EventBusScrollback = 1000
        });

        Console.WriteLine($"eventbus-s3-fraction: cli-preset fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.HasMandatorySink).IsFalse()
            .Because(
                "#478: the CLI registers no sink. The TypeFilterMiddleware it used to register was declared "
                + "Mandatory while admitting every event, so it disqualified the fast path for nothing.");
        await Assert.That(bus.FastPathEligible).IsTrue()
            .Because("no mandatory sink (#478) and an unread ring (#518) — the two terms are both clear");
        await Assert.That(fast).IsEqualTo(Publishes)
            .Because(
                "with no sink left, 1000 unread scrollback slots are the only cost this preset used to pay, "
                + "and #518 removed it");
        await Assert.That(slow).IsEqualTo(0);
    }

    /// <summary>
    ///     The desktop preset (<c>DesktopDefault</c>), and the row #518 exists for:
    ///     no sinks at all and the default scrollback capacity. Before #518 this
    ///     measured 0/200 — the 1000-slot ring was written on every publish,
    ///     read by no one, and its mere existence was the sole reason the fast
    ///     path was unreachable in the shipped desktop composition. Capacity is
    ///     now a potential rather than an obligation (retention is armed by the
    ///     first <c>GetScrollback</c>), so this row is the regression: measured,
    ///     so a future capacity or sink change shows up as a changed number
    ///     rather than as a silent topology change nobody reviewed.
    /// </summary>
    [Test]
    public async Task DesktopPreset_ZeroSubscribers_Qualifies()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory"
        });

        Console.WriteLine($"eventbus-s3-fraction: desktop-preset fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.HasMandatorySink).IsFalse();
        await Assert.That(bus.FastPathEligible).IsTrue()
            .Because("no mandatory sink, and the ring is unarmed until somebody reads it (#518)");
        await Assert.That(fast).IsEqualTo(Publishes)
            .Because("zero subscribers + no mandatory sink + no reader is the complete list of reasons a publish can be unobservable");
        await Assert.That(slow).IsEqualTo(0);
    }

    /// <summary>
    ///     The qualifying case: scrollback disabled and no sink registered. Every
    ///     publish is unobservable, every publish is counted, and the
    ///     queue-age envelope is bypassed without the total going missing.
    /// </summary>
    [Test]
    public async Task ScrollbackOff_NoSinks_EveryPublishQualifies()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            EventBusScrollback = 0
        });

        Console.WriteLine($"eventbus-s3-fraction: headless fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.FastPathEligible).IsTrue();
        await Assert.That(fast).IsEqualTo(Publishes)
            .Because("0 subscribers + no retention + no mandatory sink is the complete list of reasons a publish can be unobservable");
        await Assert.That(slow).IsEqualTo(0);
    }

    /// <summary>
    ///     A mandatory sink alone is enough — with zero subscribers and zero
    ///     scrollback, the event still has to reach the sink that declared it
    ///     mandatory, so the bus stays on the full path. The filter carries a real
    ///     allowlist (#478): a sink may only claim the mandatory verdict if it
    ///     actually restricts what the projections downstream get to see. The
    ///     published <c>TurnStartEvent</c> is on the allowlist and still passes the
    ///     filter — this row is about the SINK being mandatory, not about dropping.
    /// </summary>
    [Test]
    public async Task ScrollbackOff_MandatorySink_StillDisqualifies()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            EventBusScrollback = 0,
            EventBusMiddlewares = lf => new IEventBusMiddleware[]
            {
                new TypeFilterMiddleware(
                    lf.CreateLogger<TypeFilterMiddleware>(), typeof(TurnStartEvent))
            }
        });

        Console.WriteLine($"eventbus-s3-fraction: headless+filter fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.HasMandatorySink).IsTrue()
            .Because("TypeFilterMiddleware declares EventBusSinkKind.Mandatory (docs/EVENT_BUS_SINKS.md)");
        await Assert.That(bus.FastPathEligible).IsFalse();
        await Assert.That(fast).IsEqualTo(0);
        await Assert.That(slow).IsEqualTo(Publishes);
    }

    /// <summary>
    ///     An optional-only sink set keeps the bus eligible AND still runs the
    ///     sink: the drain counter is what distinguishes "drained" from
    ///     "silently skipped", and this is the case the pre-#47/S3 guard could
    ///     not express at all (it demanded no middleware whatsoever).
    /// </summary>
    [Test]
    public async Task ScrollbackOff_OptionalSink_Qualifies_AndDrains()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            EventBusScrollback = 0,
            EventBusMiddlewares = lf => new IEventBusMiddleware[]
            {
                new SamplingMiddleware(lf.CreateLogger<SamplingMiddleware>(), rate: 1.0)
            }
        });

        Console.WriteLine($"eventbus-s3-fraction: headless+sampler fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.FastPathEligible).IsTrue()
            .Because("SamplingMiddleware declares EventBusSinkKind.Optional — a throttle that drops events on purpose");
        await Assert.That(fast).IsEqualTo(Publishes);
        await Assert.That(bus.OptionalSinkDrainCount).IsEqualTo(Publishes)
            .Because("the optional sink was drained on every fast-path publish, not skipped");
        await Assert.That(slow).IsEqualTo(0);
    }
}
