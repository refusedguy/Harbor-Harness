using System.Diagnostics.Metrics;
using Harbor.Diagnostics;
using Harbor.Telemetry;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;

namespace Harbor.Telemetry.Tests;

/// <summary>
///     #2674 G2: the sampler exports the current process's RSS and CPU
///     utilization without a native module. Verified two ways: through a stub
///     <see cref="IMetrics" /> (stable names, values, and the CPU delta) and
///     through a real <see cref="MeterListener" /> on the canonical Meter
///     (what an OTLP host actually observes).
/// </summary>
[NotInParallel("telemetry")]
public class ResourceSamplerTests : IDisposable
{
    private const double RssBytes = 128d * 1024 * 1024;

    private readonly CapturingLogger _logger = new();
    private readonly List<(string Name, double Value)> _observed = [];
    private readonly MeterListener _meterListener;

    public ResourceSamplerTests()
    {
        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == HarborTelemetrySources.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            _observed.Add((instrument.Name, value));
        });
        _meterListener.Start();
    }

    public void Dispose() => _meterListener.Dispose();

    /// <summary>
    ///     Each report emits resident bytes and CPU utilization under stable
    ///     names. The first report has no baseline, so CPU is 0; the second
    ///     observes the delta between two probe reads.
    /// </summary>
    [Test]
    public async Task Report_EmitsWorkingSetAndCpu_UnderStableNames()
    {
        var sink = new RecordingMetrics();
        var probe = new ScriptedProbe((long)RssBytes);

        using (var sampler = new ResourceSampler(sink, _logger, probe: probe.Read))
        {
            probe.CpuTime = TimeSpan.FromMilliseconds(10);
            sampler.Report();

            probe.CpuTime = TimeSpan.FromMilliseconds(60);
            sampler.Report();
        }

        await Assert.That(probe.Calls).IsEqualTo(2);

        await Assert.That(sink.Histograms.Count(h => h.Name == TelemetryTagNames.ProcessWorkingSetBytes)).IsEqualTo(2);
        await Assert.That(sink.Histograms.First(h => h.Name == TelemetryTagNames.ProcessWorkingSetBytes).Value).IsEqualTo(RssBytes);
        await Assert.That(sink.Histograms.Count(h => h.Name == TelemetryTagNames.ProcessCpuUtilizationPercent)).IsEqualTo(2);
        await Assert.That(sink.Histograms.First(h => h.Name == TelemetryTagNames.ProcessCpuUtilizationPercent).Value).IsEqualTo(0);

        // The numbers reach the log file, so `harbor logs --last` shows them
        // with no OTLP endpoint attached.
        await Assert.That(_logger.Messages.Count).IsEqualTo(2);
        await Assert.That(_logger.Messages[0]).Contains("process resources");
        await Assert.That(_logger.Messages[0]).Contains("rss=");
    }

    /// <summary>
    ///     The values land on the canonical Meter under the canonical names, so
    ///     a <see cref="MeterListener" /> — or the OTLP exporter, which
    ///     subscribes to the same Meter by name — observes them with no extra
    ///     wiring.
    /// </summary>
    [Test]
    public async Task Report_ReachesCanonicalMeter_ForMeterListenerAndOtlp()
    {
        var probe = new ScriptedProbe((long)RssBytes);

        using var sampler = new ResourceSampler(MeterMetrics.Instance, _logger, probe: probe.Read);
        sampler.Report();

        await Assert.That(_observed.Any(e =>
            e.Name == TelemetryTagNames.ProcessWorkingSetBytes && e.Value == RssBytes)).IsTrue();
        await Assert.That(_observed.Any(e =>
            e.Name == TelemetryTagNames.ProcessCpuUtilizationPercent)).IsTrue();
    }

    /// <summary>No poller is started unless a positive interval is asked for.</summary>
    [Test]
    public async Task Constructor_WithoutInterval_DoesNotStartPolling()
    {
        var probe = new ScriptedProbe((long)RssBytes);
        var sampler = new ResourceSampler(new RecordingMetrics(), _logger, probe: probe.Read);
        await Task.Delay(30);
        sampler.Dispose();
        sampler.Dispose(); // idempotent — no poller joined, nothing thrown

        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    /// <summary>Scripted process probe — the source the sampler reads.</summary>
    private sealed class ScriptedProbe
    {
        private readonly long _rssBytes;

        public ScriptedProbe(long rssBytes) => _rssBytes = rssBytes;

        public TimeSpan CpuTime { get; set; }

        public int Calls { get; private set; }

        public ProcessResourceSnapshot Read()
        {
            Calls++;
            return new ProcessResourceSnapshot(_rssBytes, CpuTime);
        }
    }

    /// <summary>Captures the sink contract without touching the process-wide Meter.</summary>
    private sealed class RecordingMetrics : IMetrics
    {
        public List<(string Name, double Value)> Histograms { get; } = [];

        public List<(string Name, double Value)> Counters { get; } = [];

        public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags) =>
            Counters.Add((name, value));

        public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags) =>
            Histograms.Add((name, value));
    }
}
