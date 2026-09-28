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
//   * scrollback capacity alone is enough to disqualify a bus;
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
    ///     1000-slot scrollback plus the type filter. The filter is a MANDATORY
    ///     sink (a filter is a contract on what projections may see, not a
    ///     listener), so no publish may be short-circuited here — and the
    ///     measurement is the point, not the assertion of a guess.
    /// </summary>
    [Test]
    public async Task CliPreset_ZeroSubscribers_DoesNotQualify()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            EventBusScrollback = 1000,
            EventBusMiddlewares = lf => new IEventBusMiddleware[]
            {
                new TypeFilterMiddleware(lf.CreateLogger<TypeFilterMiddleware>())
            }
        });

        Console.WriteLine($"eventbus-s3-fraction: cli-preset fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.HasMandatorySink).IsTrue()
            .Because("TypeFilterMiddleware declares EventBusSinkKind.Mandatory (docs/EVENT_BUS_SINKS.md)");
        await Assert.That(bus.FastPathEligible).IsFalse();
        await Assert.That(fast).IsEqualTo(0)
            .Because("a mandatory sink and 1000 scrollback slots both rule the fast path out");
        await Assert.That(slow).IsEqualTo(Publishes);
    }

    /// <summary>
    ///     The desktop preset (<c>DesktopDefault</c>): no sinks at all, but no
    ///     scrollback override either, so the bus falls back to the library
    ///     default capacity and the fast path stays out of reach. Measured, so a
    ///     future capacity change shows up as a changed number rather than as a
    ///     silent topology change nobody reviewed.
    /// </summary>
    [Test]
    public async Task DesktopPreset_ZeroSubscribers_DoesNotQualify()
    {
        var (fast, slow, bus) = await MeasureAsync(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory"
        });

        Console.WriteLine($"eventbus-s3-fraction: desktop-preset fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

        await Assert.That(bus.HasMandatorySink).IsFalse();
        await Assert.That(fast).IsEqualTo(0)
            .Because("the desktop preset keeps the default scrollback capacity, which alone disqualifies the fast path");
        await Assert.That(slow).IsEqualTo(Publishes);
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
    ///     mandatory, so the bus stays on the full path.
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
                new TypeFilterMiddleware(lf.CreateLogger<TypeFilterMiddleware>())
            }
        });

        Console.WriteLine($"eventbus-s3-fraction: headless+filter fast={fast} slow={slow} → {fast / (double)Publishes:P2} qualify");

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
