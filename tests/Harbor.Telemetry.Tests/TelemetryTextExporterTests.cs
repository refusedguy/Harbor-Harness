using System.Diagnostics.Metrics;
using Harbor.Diagnostics;
using Harbor.Telemetry;
using Microsoft.Extensions.Logging;

namespace Harbor.Telemetry.Tests;

/// <summary>
///     #1741/#1742: the visible telemetry sink.
///     <para>
///         The four properties this suite pins are the ones the owner asked
///         for: the activity IS visible when the sink is on, the byte cap
///         produces a counter and a log line instead of silence, a secret
///         never reaches the sink, and the default run produces nothing at
///         all.
///     </para>
/// </summary>
[NotInParallel("telemetry")]
public class TelemetryTextExporterTests : IDisposable
{
    private readonly CapturingLogger _logger = new();
    private readonly List<(string Name, double Value)> _observed = [];
    private readonly MeterListener _meterListener;

    public TelemetryTextExporterTests()
    {
        // The same observation an external collector makes: a MeterListener on
        // the canonical meter, so the export's OWN counters are visible to
        // whoever watches the meter.
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == HarborTelemetrySources.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _observed.Add((instrument.Name, value)));
        _meterListener.Start();
    }

    public void Dispose() => _meterListener.Dispose();

    /// <summary>A sink that writes to the given writer and never flushes on its own.</summary>
    private static TelemetryExportOptions Options(
        int maxRecordBytes = TelemetryExportOptions.DefaultMaxRecordBytes,
        int maxBufferBytes = TelemetryExportOptions.DefaultMaxBufferBytes,
        int maxBufferedRecords = TelemetryExportOptions.DefaultMaxBufferedRecords) => new()
    {
        Target = TelemetryExportTarget.Console,
        FlushInterval = null,
        MaxRecordBytes = maxRecordBytes,
        MaxBufferBytes = maxBufferBytes,
        MaxBufferedRecords = maxBufferedRecords,
    };

    /// <summary>
    ///     The whole point of the sink: a finished span shows up, with its
    ///     tags, in the output the host asked for.
    /// </summary>
    [Test]
    public async Task Enabled_AFinishedActivity_ReachesTheSink()
    {
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(Options(), _logger, output);

        using (ITelemetrySpan? span = ActivityTracer.Instance.StartSpan(
                   "tool.execute",
                   new KeyValuePair<string, object?>("tool.name", "read")))
        {
            span?.SetTag("tool.status", "ok");
        }

        exporter.Flush();

        string text = output.ToString();
        await Assert.That(text).Contains("tool.execute");
        await Assert.That(text).Contains("tool.name");
        await Assert.That(text).Contains("read");
        await Assert.That(text).Contains("duration_ms");
    }

    /// <summary>Measurements reach the sink too — the sink is not a span-only view.</summary>
    [Test]
    public async Task Enabled_AMeasurement_ReachesTheSink()
    {
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(Options(), _logger, output);

        MeterMetrics.Instance.Counter(
            "tests.telemetry.visible.counter",
            1,
            new KeyValuePair<string, object?>("kind", "unit"));
        exporter.Flush();

        string text = output.ToString();
        await Assert.That(text).Contains("tests.telemetry.visible.counter");
        await Assert.That(text).Contains("kind");
    }

    /// <summary>
    ///     The redaction gate: a credential does not reach the sink, neither
    ///     under a key that names it nor under a key that does not.
    /// </summary>
    [Test]
    public async Task Enabled_ASecretUnderAnyKey_NeverReachesTheSink()
    {
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(Options(), _logger, output);

        exporter.TryExport("activity", "llm.stream", [
            new KeyValuePair<string, object?>("api_key", "sk-abcdef0123456789"),
            new KeyValuePair<string, object?>("note", "Bearer topsecrettokenvalue"),
        ]);
        exporter.Flush();

        string text = output.ToString();
        await Assert.That(text).DoesNotContain("sk-abcdef0123456789");
        await Assert.That(text).DoesNotContain("topsecrettokenvalue");
        await Assert.That(text).Contains("[redacted]");
    }

    /// <summary>
    ///     The byte cap is not silent: a record that does not fit is dropped,
    ///     the drop is counted on the metrics surface, and the log says so.
    /// </summary>
    [Test]
    public async Task OverTheByteCap_TheDropIsCountedAndLogged()
    {
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(
            Options(maxBufferBytes: 1024, maxBufferedRecords: 100_000),
            _logger,
            output);

        string payload = new('x', 512);
        for (int i = 0; i < 64; i++)
        {
            exporter.TryExport("activity", "tool.execute", [new KeyValuePair<string, object?>("payload", payload)]);
        }

        TelemetryExportStats stats = exporter.Stats;
        await Assert.That(stats.DroppedRecords).IsGreaterThan(0L);
        await Assert.That(stats.DroppedBytes).IsGreaterThan(0L);
        await Assert.That(_observed.Any(o => o.Name == TelemetryTagNames.ExportDropped && o.Value > 0)).IsTrue();
        await Assert.That(_logger.Messages.Any(m => m.Contains("dropped"))).IsTrue();
    }

    /// <summary>
    ///     A record longer than the per-record cap is shortened, and the
    ///     shortening is counted — a truncated record is not a lost record.
    /// </summary>
    [Test]
    public async Task OverTheRecordCap_TheRecordIsTruncatedAndCounted()
    {
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(
            Options(maxRecordBytes: 128),
            _logger,
            output);

        exporter.TryExport("activity", "tool.execute", [
            new KeyValuePair<string, object?>("payload", new string('x', 4096)),
        ]);
        exporter.Flush();

        TelemetryExportStats stats = exporter.Stats;
        await Assert.That(stats.TruncatedRecords).IsEqualTo(1L);
        await Assert.That(stats.TruncatedBytes).IsGreaterThan(0L);
        await Assert.That(_observed.Any(o => o.Name == TelemetryTagNames.ExportTruncated)).IsTrue();
    }

    /// <summary>
    ///     The default is "no sink": the sink is inert, no listener is
    ///     attached, and the tracer creates no span at all — so a normal run
    ///     writes nothing and allocates nothing.
    /// </summary>
    [Test]
    public async Task Disabled_ByDefault_TheSinkIsInert()
    {
        await Assert.That(new TelemetryExportOptions().Target).IsEqualTo(TelemetryExportTarget.None);

        using var exporter = new TelemetryTextExporter(new TelemetryExportOptions(), _logger);

        ITelemetrySpan? span = ActivityTracer.Instance.StartSpan("tool.execute");
        await Assert.That(span).IsNull();
    }

    /// <summary>The file target appends one line per record to the given path.</summary>
    [Test]
    public async Task FileTarget_WritesTheRecordToTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-telemetry-{Guid.NewGuid():N}.txt");
        try
        {
            using (var exporter = new TelemetryTextExporter(
                       new TelemetryExportOptions
                       {
                           Target = TelemetryExportTarget.File,
                           FilePath = path,
                           FlushInterval = null,
                       },
                       _logger))
            {
                exporter.TryExport("metric", "tool.calls", [new KeyValuePair<string, object?>("tool.name", "read")]);
                exporter.Flush();
            }

            string text = await File.ReadAllTextAsync(path);
            await Assert.That(text).Contains("tool.calls");
            await Assert.That(text).Contains("read");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Minimal logger that keeps the rendered lines for assertions.</summary>
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
