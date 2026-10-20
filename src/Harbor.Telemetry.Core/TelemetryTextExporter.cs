using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Telemetry;

/// <summary>
///     A visible, bounded sink for Harbor's own telemetry: it observes the
///     canonical <see cref="ActivitySource" /> and <see cref="Meter" /> and
///     writes one line per record — to the console or to a file.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Harbor's instrumentation was complete and
///         invisible: <see cref="ActivityTracer" /> and
///         <see cref="MeterMetrics" /> write into the canonical source and
///         meter, and with no listener attached every span and every
///         measurement went nowhere. This class is the listener that makes
///         them visible WITHOUT the OpenTelemetry SDK — it is built on
///         <see cref="ActivityListener" /> and <see cref="MeterListener" />,
///         which are part of the framework, so the AOT publish of the CLI
///         carries no OTLP exporter and no protobuf/grpc stack.
///     </para>
///     <para>
///         <b>Opt-in, and inert when off.</b> The sink exists only when a host
///         constructs it — the environment variable
///         <c>HARBOR_TELEMETRY_EXPORT</c> (<c>console</c> / <c>file</c>), or
///         the composition option
///         <c>HarborComposeOptions.TelemetryExport</c>.
///         A sink whose target is <see cref="TelemetryExportTarget.None" />
///         attaches no listener at all, so
///         <see cref="ActivitySource.StartActivity(string)" /> keeps returning
///         <see langword="null" /> and the instrumentation stays
///         allocation-free.
///     </para>
///     <para>
///         <b>Bounded, and honest about the bound.</b> A record longer than
///         <see cref="TelemetryExportOptions.MaxRecordBytes" /> is truncated;
///         a record that does not fit the buffer is dropped. Both are counted
///         (<see cref="Stats" />) and published on the canonical meter
///         (<c>telemetry.export.dropped</c> and friends) — a lost record is
///         never a silent loss.
///     </para>
///     <para>
///         <b>One gate.</b> Every attribute written here passes through
///         <see cref="TelemetryRedaction" />, the single choke point that
///         decides what may leave the process.
///     </para>
/// </remarks>
public sealed partial class TelemetryTextExporter : IDisposable
{
    /// <summary>
    ///     The export's own counters. Static instrument references created
    ///     once: the sink must not pay a per-call instrument lookup on the
    ///     path it exists to make visible. The prefix is what keeps the sink
    ///     from observing its own counters and feeding them back into itself.
    /// </summary>
    private static readonly Counter<double> RecordsCounter =
        HarborTelemetrySources.Instruments.CreateCounter<double>(TelemetryTagNames.ExportRecords);

    private static readonly Counter<double> BytesCounter =
        HarborTelemetrySources.Instruments.CreateCounter<double>(TelemetryTagNames.ExportBytes);

    private static readonly Counter<double> DroppedCounter =
        HarborTelemetrySources.Instruments.CreateCounter<double>(TelemetryTagNames.ExportDropped);

    private static readonly Counter<double> DroppedBytesCounter =
        HarborTelemetrySources.Instruments.CreateCounter<double>(TelemetryTagNames.ExportDroppedBytes);

    private static readonly Counter<double> TruncatedCounter =
        HarborTelemetrySources.Instruments.CreateCounter<double>(TelemetryTagNames.ExportTruncated);

    private static readonly Counter<double> TruncatedBytesCounter =
        HarborTelemetrySources.Instruments.CreateCounter<double>(TelemetryTagNames.ExportTruncatedBytes);

    private readonly TelemetryExportOptions _options;
    private readonly ILogger _logger;
    private readonly TextWriter? _output;
    private readonly ActivityListener? _activityListener;
    private readonly MeterListener? _meterListener;
    private CancellationTokenSource? _shutdown;
    private Task? _flushLoop;

    private readonly object _bufferGate = new();
    private readonly Queue<PendingRecord> _pending = new();
    private long _pendingBytes;

    private readonly object _writeGate = new();
    private StreamWriter? _file;

    private long _exportedRecords;
    private long _exportedBytes;
    private long _droppedRecords;
    private long _droppedBytes;
    private long _truncatedRecords;
    private long _truncatedBytes;
    private long _dropEvents;

    private bool _disposed;

    /// <summary>
    ///     Create a sink and attach the listeners. The listeners are live from
    ///     this constructor on, so a span created afterwards is captured.
    /// </summary>
    /// <param name="options">Where to write and how much to hold.</param>
    /// <param name="logger">Logger the drop/write diagnostics go to.</param>
    /// <param name="output">
    ///     Optional writer that receives the records instead of the configured
    ///     target. The host-injection seam: a host that owns its console (a
    ///     TUI, a test) hands the sink a writer and keeps the output intact.
    /// </param>
    public TelemetryTextExporter(
        TelemetryExportOptions options,
        ILogger logger,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options with
        {
            MaxRecordBytes = Math.Max(64, options.MaxRecordBytes),
            MaxBufferBytes = Math.Max(1024, options.MaxBufferBytes),
            MaxBufferedRecords = Math.Max(1, options.MaxBufferedRecords),
        };
        _logger = logger;
        _output = output;

        // No target and no injected writer means "no sink at all": attaching
        // listeners here would make the whole process pay for telemetry nobody
        // asked to see.
        if (options.Target == TelemetryExportTarget.None && output is null)
        {
            return;
        }

        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == HarborTelemetrySources.SourceName,
            Sample = SampleAllData,
            ActivityStopped = OnActivityStopped,
        };

        // AddActivityListener returns the listener it registered; there is no
        // failure mode to report — the listener is live from here on.
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = OnInstrumentPublished,
        };

        // The canonical meter only publishes double instruments today
        // (MeterMetrics creates Counter<double>/Histogram<double>), but the
        // long/int callbacks cost nothing and keep the sink honest if that
        // ever changes.
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
        _meterListener.Start();

        if (options.FlushInterval is { } interval && interval > TimeSpan.Zero)
        {
            _shutdown = new CancellationTokenSource();
            CancellationToken token = _shutdown.Token;
            _flushLoop = Task.Run(() => FlushLoopAsync(interval, token), CancellationToken.None);
        }
    }

    /// <summary>What the sink has done so far: exported, dropped and truncated counts.</summary>
    public TelemetryExportStats Stats
    {
        get
        {
            int pendingRecords;
            long pendingBytes;
            lock (_bufferGate)
            {
                pendingRecords = _pending.Count;
                pendingBytes = _pendingBytes;
            }

            return new TelemetryExportStats(
                Interlocked.Read(ref _exportedRecords),
                Interlocked.Read(ref _exportedBytes),
                Interlocked.Read(ref _droppedRecords),
                Interlocked.Read(ref _droppedBytes),
                Interlocked.Read(ref _truncatedRecords),
                Interlocked.Read(ref _truncatedBytes),
                pendingRecords,
                pendingBytes);
        }
    }

    /// <summary>
    ///     Export one record. The name and every attribute pass through
    ///     <see cref="TelemetryRedaction" /> before anything is written.
    /// </summary>
    /// <param name="kind">Record kind — <c>activity</c> or <c>metric</c>.</param>
    /// <param name="name">Record name — a span name or an instrument name.</param>
    /// <param name="attributes">Attributes to export with the record.</param>
    /// <returns>
    ///     <see langword="true" /> when the record was accepted into the
    ///     buffer; <see langword="false" /> when it was dropped (the drop is
    ///     counted and logged).
    /// </returns>
    public bool TryExport(string kind, string name, IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(attributes);

        if (_disposed || _activityListener is null)
        {
            return false;
        }

        return Accept(RenderLine(kind, name, attributes));
    }

    /// <summary>Write everything currently buffered to the sink.</summary>
    public void Flush()
    {
        List<PendingRecord> batch;
        lock (_bufferGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = new List<PendingRecord>(_pending.Count);
            while (_pending.Count > 0)
            {
                batch.Add(_pending.Dequeue());
            }

            _pendingBytes = 0;
        }

        long bytes = 0;
        foreach (PendingRecord record in batch)
        {
            WriteLine(record.Line);
            bytes += record.Size;
        }

        Interlocked.Add(ref _exportedRecords, batch.Count);
        Interlocked.Add(ref _exportedBytes, bytes);
        RecordsCounter.Add(batch.Count);
        BytesCounter.Add(bytes);
    }

    /// <summary>Stop the listeners, flush what is buffered and release the file.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource? shutdown = _shutdown;
        _shutdown = null;
        if (shutdown is not null)
        {
            shutdown.Cancel();

            // Join the loop so its outcome is always observed — never
            // fire-and-forget (§FP-003). It exits on the next tick wait, so the
            // bound is generous; a slow join must not block host shutdown.
            Task? loop = _flushLoop;
            _flushLoop = null;
            if (loop is not null)
            {
                try
                {
                    loop.Wait(FlushJoinTimeout);
                }
                catch (AggregateException)
                {
                    // The loop already logged its own failure; disposal stays
                    // best-effort and never masks it.
                }
            }

            shutdown.Dispose();
        }

        Flush();

        _activityListener?.Dispose();
        _meterListener?.Dispose();

        lock (_writeGate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>Bound on the Dispose join — the loop only waits for a tick.</summary>
    private static readonly TimeSpan FlushJoinTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Render one record as a single line, through the redaction gate.
    /// </summary>
    private static string RenderLine(string kind, string name, IReadOnlyList<KeyValuePair<string, object?>> attributes)
    {
        StringBuilder builder = new(160);
        builder.Append(TelemetryRedaction.SanitizeName(kind)).Append(' ');
        builder.Append(TelemetryRedaction.SanitizeName(name));

        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            AppendAttribute(builder, attribute.Key, attribute.Value);
        }

        return builder.ToString();
    }

    /// <summary>Append one attribute, rendered by the redaction gate.</summary>
    private static void AppendAttribute(StringBuilder builder, string? key, object? value)
    {
        string safeKey = key ?? string.Empty;
        builder.Append(' ').Append(TelemetryRedaction.SanitizeName(safeKey));
        builder.Append('=').Append('"');
        builder.Append(TelemetryRedaction.Apply(safeKey, value));
        builder.Append('"');
    }

    /// <summary>
    ///     Take one rendered record: truncate it to the per-record cap, then
    ///     either buffer it or drop it (and say so).
    /// </summary>
    private bool Accept(string line)
    {
        int size = Encoding.UTF8.GetByteCount(line);
        if (size > _options.MaxRecordBytes)
        {
            int excess = size - _options.MaxRecordBytes;
            line = TruncateToByteBudget(line, _options.MaxRecordBytes);
            size = Encoding.UTF8.GetByteCount(line);
            Interlocked.Add(ref _truncatedRecords, 1);
            Interlocked.Add(ref _truncatedBytes, excess);
            TruncatedCounter.Add(1);
            TruncatedBytesCounter.Add(excess);
        }

        bool accepted;
        lock (_bufferGate)
        {
            accepted = _pending.Count < _options.MaxBufferedRecords
                && _pendingBytes + size <= _options.MaxBufferBytes;
            if (accepted)
            {
                _pending.Enqueue(new PendingRecord(line, size));
                _pendingBytes += size;
            }
        }

        if (!accepted)
        {
            ReportDrop(size);
            return false;
        }

        // With a background flusher running, a buffer that is a quarter full is
        // written out now: the common case then never holds a record longer
        // than one tick, and a host that stops without disposing the sink
        // loses at most one tick of records.
        if (_flushLoop is not null)
        {
            bool shouldFlush;
            lock (_bufferGate)
            {
                shouldFlush = _pendingBytes >= _options.MaxBufferBytes / 4;
            }

            if (shouldFlush)
            {
                Flush();
            }
        }

        return true;
    }

    /// <summary>Cut a line down to a byte budget without splitting a UTF-8 sequence.</summary>
    private static string TruncateToByteBudget(string line, int budget)
    {
        if (budget <= 0)
        {
            return string.Empty;
        }

        int length = Math.Min(line.Length, budget);
        while (length > 0 && Encoding.UTF8.GetByteCount(line.AsSpan(0, length)) > budget)
        {
            length--;
        }

        return line[..length];
    }

    /// <summary>
    ///     Sample every span of the canonical source. The sink needs the tags
    ///     and the duration, so it asks for all data — and with a listener
    ///     attached <see cref="ActivitySource.StartActivity(string)" /> stops
    ///     returning <see langword="null" />, which is the whole point of the
    ///     sink being opt-in.
    /// </summary>
    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllDataAndRecorded;

    /// <summary>A finished span becomes one exported record.</summary>
    private void OnActivityStopped(Activity activity)
    {
        if (_disposed || !_options.IncludeActivities)
        {
            return;
        }

        List<KeyValuePair<string, object?>> attributes = new(8)
        {
            new("duration_ms", activity.Duration.TotalMilliseconds),
            new("status", activity.Status.ToString()),
        };

        foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
        {
            attributes.Add(tag);
        }

        Accept(RenderLine("activity", activity.OperationName, attributes));
    }

    /// <summary>A measurement becomes one exported record.</summary>
    private void OnMeasurement<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        if (_disposed || !_options.IncludeMetrics)
        {
            return;
        }

        StringBuilder builder = new(160);
        builder.Append(TelemetryRedaction.SanitizeName("metric")).Append(' ');
        builder.Append(TelemetryRedaction.SanitizeName(instrument.Name));
        AppendAttribute(builder, "value", Convert.ToDouble(value, CultureInfo.InvariantCulture));
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            AppendAttribute(builder, tag.Key, tag.Value);
        }

        Accept(builder.ToString());
    }

    /// <summary>
    ///     Enable the instruments of the canonical meter — and only those: the
    ///     export counters this sink publishes must not feed back into it.
    /// </summary>
    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name != HarborTelemetrySources.SourceName)
        {
            return;
        }

        if (instrument.Name.StartsWith(TelemetryTagNames.ExportInstrumentPrefix, StringComparison.Ordinal))
        {
            return;
        }

        listener.EnableMeasurementEvents(instrument);
    }

    /// <summary>Write one line to the sink, counting a write that fails as a lost record.</summary>
    private void WriteLine(string line)
    {
        lock (_writeGate)
        {
            try
            {
                if (_output is not null)
                {
                    _output.WriteLine(line);
                    _output.Flush();
                    return;
                }

                if (_options.Target == TelemetryExportTarget.File)
                {
                    StreamWriter file = EnsureFile();
                    file.WriteLine(line);
                    file.Flush();
                    return;
                }

                Console.Out.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
            {
                // A failing sink must not take the process down — but the lost
                // record is still counted, never swallowed.
                Interlocked.Add(ref _droppedRecords, 1);
                DroppedCounter.Add(1);
                LogWriteFailed(_logger, ex);
            }
        }
    }

    /// <summary>Open the export file on first use; the directory is created for it.</summary>
    private StreamWriter EnsureFile()
    {
        if (_file is not null)
        {
            return _file;
        }

        string path = _options.FilePath
            ?? throw new InvalidOperationException("A file telemetry export needs a file path.");

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        return _file;
    }

    /// <summary>
    ///     Report a dropped record: an exact counter on the metrics surface and
    ///     a warning line in the log. The line is rate-limited (the first drop
    ///     and every hundredth after it) so a burst cannot flood the log — the
    ///     running totals are in every line, and the counter is exact.
    /// </summary>
    private void ReportDrop(int size)
    {
        long dropped = Interlocked.Increment(ref _droppedRecords);
        Interlocked.Add(ref _droppedBytes, size);
        DroppedCounter.Add(1);
        DroppedBytesCounter.Add(size);

        long events = Interlocked.Increment(ref _dropEvents);
        if (events != 1 && events % 100 != 0)
        {
            return;
        }

        LogDropped(
            _logger,
            dropped,
            Interlocked.Read(ref _droppedBytes),
            _options.MaxBufferBytes,
            _options.MaxBufferedRecords);
    }

    /// <summary>Background writer: wakes on the tick and writes the buffer out.</summary>
    private async Task FlushLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal — the expected exit.
        }
        catch (Exception ex)
        {
            // Telemetry must never take the process down, and its errors must
            // never vanish silently (§FP-003).
            LogFlushFailed(_logger, ex);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Telemetry export dropped {DroppedRecords} record(s) ({DroppedBytes} bytes) — the export buffer is full ({MaxBufferBytes} bytes / {MaxRecords} records).")]
    private static partial void LogDropped(ILogger logger, long droppedRecords, long droppedBytes, int maxBufferBytes, int maxRecords);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Telemetry export write failed — the record was lost by the sink.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Telemetry export flushing stopped after an error.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);

    /// <summary>One record waiting in the buffer.</summary>
    private readonly record struct PendingRecord(string Line, int Size);
}
