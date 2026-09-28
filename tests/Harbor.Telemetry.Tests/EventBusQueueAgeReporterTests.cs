using System.Diagnostics.Metrics;
using Harbor.Abstractions.Events;
using Harbor.Diagnostics;
using Harbor.Telemetry;
using Microsoft.Extensions.Logging;

namespace Harbor.Telemetry.Tests;

/// <summary>
///     #47/S2: the reporter is the seam that carries the event-bus publish
///     envelope (p50/p95/p99/max + oldest-pending + inflight + published) out of
///     <c>Harbor.Registries</c> and into the metrics surface, without a
///     forbidden project reference. Verified two ways: through a stub
///     <see cref="IMetrics" /> (exactly what is asked of the sink, under which
///     stable names/tags) and through a real <see cref="MeterListener" /> on the
///     canonical Meter (what an OTLP host actually observes).
/// </summary>
[NotInParallel("telemetry")]
public class EventBusQueueAgeReporterTests : IDisposable
{
    private const double P50Ms = 0.5;
    private const double P95Ms = 12;
    private const double P99Ms = 90;
    private const double MaxMs = 570;

    private readonly CapturingLogger _logger = new();
    private readonly List<(string Name, double Value, string? Quantile)> _observed = [];
    private readonly MeterListener _meterListener;

    public EventBusQueueAgeReporterTests()
    {
        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == HarborTelemetrySources.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? quantile = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == TelemetryTagNames.DispatchQuantile)
                {
                    quantile = tag.Value?.ToString();
                }
            }

            _observed.Add((instrument.Name, value, quantile));
        });
        _meterListener.Start();
    }

    public void Dispose() => _meterListener.Dispose();

    /// <summary>
    ///     One report emits the whole exposed set under stable names: the
    ///     envelope distribution tagged p50/p95/p99/max, the live oldest-pending
    ///     age, the inflight count, and the publish counter advanced by the delta
    ///     since the previous report.
    /// </summary>
    [Test]
    public async Task Report_EmitsFullQueueAgeSet_UnderStableNames()
    {
        var sink = new RecordingMetrics();

        // 30 publishes already on the bus when the reporter is constructed —
        // they are the counter baseline, not part of the first report.
        var source = new StubQueueMetrics
        {
            InflightPublishCount = 2,
            OldestPendingAge = TimeSpan.FromMilliseconds(33),
            MaxDispatchDuration = TimeSpan.FromMilliseconds(MaxMs),
            DispatchSampleCount = 40,
        };

        using (var reporter = new EventBusQueueAgeReporter(source, sink, _logger))
        {
            source.PublishedCount = 40; // 10 publishes since wiring

            reporter.Report();

            // A second report must not re-count the publishes already reported.
            reporter.Report();
        }

        // Three percentile reads per report — the distribution is polled, not
        // recomputed per instrument.
        await Assert.That(source.RequestedQuantiles.Count).IsEqualTo(6);
        await Assert.That(source.RequestedQuantiles[0]).IsEqualTo(0.50);
        await Assert.That(source.RequestedQuantiles[1]).IsEqualTo(0.95);
        await Assert.That(source.RequestedQuantiles[2]).IsEqualTo(0.99);

        await Assert.That(Quantile(sink, "p50")).IsEqualTo(P50Ms);
        await Assert.That(Quantile(sink, "p95")).IsEqualTo(P95Ms);
        await Assert.That(Quantile(sink, "p99")).IsEqualTo(P99Ms);
        await Assert.That(Quantile(sink, "max")).IsEqualTo(MaxMs);

        await Assert.That(sink.Histograms.Any(h =>
            h.Name == TelemetryTagNames.EventBusOldestPendingAgeMs && h.Value == 33)).IsTrue();
        await Assert.That(sink.Histograms.Any(h =>
            h.Name == TelemetryTagNames.EventBusPublishInflight && h.Value == 2)).IsTrue();

        // 40 publishes minus the 30 the stub had before wiring = 10, counted once.
        await Assert.That(sink.Counters.Count).IsEqualTo(1);
        await Assert.That(sink.Counters[0].Name).IsEqualTo(TelemetryTagNames.EventBusPublishCount);
        await Assert.That(sink.Counters[0].Value).IsEqualTo(10);

        // The same numbers reach the log file, so `harbor logs --last` shows them
        // with no debugger and no OTLP endpoint attached.
        await Assert.That(_logger.Messages.Count).IsEqualTo(2);
        await Assert.That(_logger.Messages[0]).Contains("eventbus queue age");
        await Assert.That(_logger.Messages[0]).Contains("p99=");
        await Assert.That(_logger.Messages[0]).Contains("oldest-pending=");
        await Assert.That(_logger.Messages[0]).Contains("inflight=2");
    }

    /// <summary>
    ///     The values land on the canonical Meter under the canonical names, so a
    ///     <see cref="MeterListener" /> — or the OTLP exporter, which subscribes
    ///     to the same Meter by name — observes the queue-age set with no further
    ///     wiring.
    /// </summary>
    [Test]
    public async Task Report_ReachesCanonicalMeter_ForMeterListenerAndOtlp()
    {
        // 30 publishes predate the wiring; 7 more land before the report.
        var source = new StubQueueMetrics
        {
            InflightPublishCount = 0,
            OldestPendingAge = TimeSpan.Zero,
            MaxDispatchDuration = TimeSpan.FromMilliseconds(MaxMs),
            DispatchSampleCount = 7,
        };

        using var reporter = new EventBusQueueAgeReporter(source, MeterMetrics.Instance, _logger);
        source.PublishedCount = 37; // 7 more than the 30 the wiring saw
        reporter.Report();

        await Assert.That(_observed.Any(e =>
            e.Name == TelemetryTagNames.EventBusPublishCount && e.Value == 7)).IsTrue();
        await Assert.That(_observed.Any(e =>
            e.Name == TelemetryTagNames.EventBusDispatchDurationMs && e.Quantile == "p99"
            && e.Value == P99Ms)).IsTrue();
        await Assert.That(_observed.Any(e =>
            e.Name == TelemetryTagNames.EventBusOldestPendingAgeMs)).IsTrue();
        await Assert.That(_observed.Any(e =>
            e.Name == TelemetryTagNames.EventBusPublishInflight && e.Value == 0)).IsTrue();
    }

    /// <summary>No poller is started unless a positive interval is asked for.</summary>
    [Test]
    public async Task Constructor_WithoutInterval_DoesNotStartPolling()
    {
        var source = new StubQueueMetrics();
        var reporter = new EventBusQueueAgeReporter(source, new RecordingMetrics(), _logger);
        await Task.Delay(30);
        reporter.Dispose();
        reporter.Dispose(); // idempotent — no poller joined, nothing thrown

        await Assert.That(source.RequestedQuantiles).IsEmpty();
    }

    private static double Quantile(RecordingMetrics sink, string quantile) => sink.Histograms
        .First(h => h.Name == TelemetryTagNames.EventBusDispatchDurationMs && h.Quantile == quantile)
        .Value;

    /// <summary>Scripted queue-age source — the port the reporter polls.</summary>
    private sealed class StubQueueMetrics : IEventBusQueueMetrics
    {
        private const int SampleCapacity = 256;

        public long PublishedCount { get; set; } = 30;

        public long InflightPublishCount { get; set; }

        public TimeSpan OldestPendingAge { get; set; }

        public TimeSpan MaxDispatchDuration { get; set; }

        public int DispatchSampleCount { get; set; }

        public int DispatchSampleCapacity => SampleCapacity;

        public List<double> RequestedQuantiles { get; } = [];

        public TimeSpan DispatchDurationPercentile(double quantile)
        {
            RequestedQuantiles.Add(quantile);
            double ms = quantile switch
            {
                >= 0.99 => P99Ms,
                >= 0.95 => P95Ms,
                _ => P50Ms,
            };
            return TimeSpan.FromMilliseconds(ms);
        }
    }

    /// <summary>Captures the sink contract without touching the process-wide Meter.</summary>
    private sealed class RecordingMetrics : IMetrics
    {
        public List<(string Name, double Value, string? Quantile)> Histograms { get; } = [];

        public List<(string Name, double Value)> Counters { get; } = [];

        public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags) =>
            Counters.Add((name, value));

        public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags)
        {
            string? quantile = null;
            foreach (KeyValuePair<string, object?> tag in tags ?? [])
            {
                if (tag.Key == TelemetryTagNames.DispatchQuantile)
                {
                    quantile = tag.Value?.ToString();
                }
            }

            Histograms.Add((name, value, quantile));
        }
    }

    /// <summary>Minimal logger that keeps the rendered snapshot line for assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
