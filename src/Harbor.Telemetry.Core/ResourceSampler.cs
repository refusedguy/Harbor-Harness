using System.Diagnostics;
using System.Diagnostics.Metrics;
using Harbor.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Harbor.Telemetry;

/// <summary>
///     One process-resource sample: resident bytes and cumulative CPU time.
///     The shape is intentionally tiny so a host can inject a scripted probe
///     and a test can drive the sampler without reading the real process.
/// </summary>
/// <param name="WorkingSetBytes">Current resident set size, from <see cref="Process.WorkingSet64" />.</param>
/// <param name="CpuTime">Cumulative processor time, from <see cref="Process.TotalProcessorTime" />.</param>
public readonly record struct ProcessResourceSnapshot(long WorkingSetBytes, TimeSpan CpuTime);

/// <summary>
///     Samples the current process's RSS and CPU utilization into Harbor's
///     canonical <see cref="Meter" /> (#2674 G2). The T3 original needs a
///     native resource monitor; on .NET a single background
///     <see cref="PeriodicTimer" /> over <see cref="Process" /> is enough, and
///     it stays AOT-safe: no reflection, no native module, no OTEL SDK.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a histogram for a gauge.</b> <see cref="IMetrics" /> exposes
///         counters and histograms only. A gauge-style "current value" is
///         recorded as one histogram sample per tick, which a
///         <see cref="MeterListener" /> / the OTLP exporter observes with no
///         extra wiring. The instrument names live on
///         <see cref="TelemetryTagNames" />, like every other Harbor metric.
///     </para>
///     <para>
///         <b>How it is wired.</b> <c>Harbor.Hosting</c> builds one eagerly in
///         <c>TelemetryModule</c>, the same way it builds the event-bus
///         queue-age reporter. The container disposes it with the host, which
///         cancels and joins the loop. A host that passes no positive interval
///         gets an inert sampler that only writes on an explicit
///         <see cref="Report" /> call (tests, one-shot diagnostics).
///     </para>
///     <para>
///         <b>CPU is a delta.</b> <see cref="Process.TotalProcessorTime" /> is
///         cumulative, so utilization is (cpu delta / wall delta /
///         <see cref="Environment.ProcessorCount" />) clamped to 0..100. The
///         first report has no baseline and records 0 rather than guessing.
///     </para>
/// </remarks>
public sealed class ResourceSampler : IDisposable
{
    /// <summary>Default poll cadence used by the CLI/desktop presets.</summary>
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(30);

    /// <summary>Upper clamp for the poll cadence (<c>PeriodicTimer</c> is uint-milliseconds).</summary>
    private static readonly TimeSpan MaxSampleInterval = TimeSpan.FromDays(1);

    /// <summary>Bound on the Dispose join — the loop only waits for a tick.</summary>
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(1);

    private readonly IMetrics _metrics;
    private readonly ILogger _logger;
    private readonly Func<ProcessResourceSnapshot> _probe;

    private CancellationTokenSource? _shutdown;
    private Task? _pollTask;
    private long _lastTimestamp;
    private TimeSpan _lastCpuTime;
    private bool _disposed;

    /// <summary>
    ///     Create a sampler. When <paramref name="sampleInterval" /> is a
    ///     positive <see cref="TimeSpan" />, the sampler starts its own cadence;
    ///     pass <c>null</c> for a host that calls <see cref="Report" /> itself.
    /// </summary>
    /// <param name="metrics">Instrument sink — normally <see cref="MeterMetrics.Instance" />.</param>
    /// <param name="logger">Category logger; the snapshot line is written at Debug.</param>
    /// <param name="sampleInterval">Cadence for the background loop; <c>null</c> or non-positive disables it.</param>
    /// <param name="probe">Resource probe; defaults to the current process. Injectable for tests.</param>
    public ResourceSampler(
        IMetrics metrics,
        ILogger logger,
        TimeSpan? sampleInterval = null,
        Func<ProcessResourceSnapshot>? probe = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _metrics = metrics;
        _logger = logger;
        _probe = probe ?? SampleCurrentProcess;

        if (sampleInterval is { } requested && requested > TimeSpan.Zero)
        {
            TimeSpan interval = requested < MaxSampleInterval ? requested : MaxSampleInterval;
            _shutdown = new CancellationTokenSource();
            CancellationToken token = _shutdown.Token;
            _pollTask = Task.Run(() => PollAsync(interval, token), CancellationToken.None);
        }
    }

    /// <summary>
    ///     Emit one resource snapshot: resident bytes and CPU utilization since
    ///     the previous report, plus one Debug log line so
    ///     <c>harbor logs --last</c> shows the numbers with no OTLP endpoint.
    ///     Safe to call repeatedly; not re-entrant (the background loop is the
    ///     only caller in hosted processes).
    /// </summary>
    public void Report()
    {
        ProcessResourceSnapshot snapshot = _probe();
        long timestamp = Stopwatch.GetTimestamp();

        double cpuPercent = 0;
        if (_lastTimestamp != 0 && timestamp > _lastTimestamp)
        {
            double elapsedMs = (timestamp - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            double cpuMs = (snapshot.CpuTime - _lastCpuTime).TotalMilliseconds;
            if (elapsedMs > 0)
            {
                cpuPercent = Math.Clamp(cpuMs / elapsedMs / Environment.ProcessorCount * 100.0, 0, 100);
            }
        }

        _lastTimestamp = timestamp;
        _lastCpuTime = snapshot.CpuTime;

        _metrics.Histogram(TelemetryTagNames.ProcessWorkingSetBytes, snapshot.WorkingSetBytes);
        _metrics.Histogram(TelemetryTagNames.ProcessCpuUtilizationPercent, cpuPercent);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "process resources: rss={RssBytes}B ({RssMiB:F1}MiB) cpu={CpuPercent:F1}%",
                snapshot.WorkingSetBytes,
                snapshot.WorkingSetBytes / (1024.0 * 1024.0),
                cpuPercent);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource? shutdown = _shutdown;
        _shutdown = null;
        if (shutdown is null)
        {
            return;
        }

        shutdown.Cancel();

        // Join the loop so its outcome is always observed — never
        // fire-and-forget (§FP-003). It exits on the next tick wait, so the
        // bound is generous; a slow join must not block host shutdown.
        Task? poll = _pollTask;
        _pollTask = null;
        if (poll is not null)
        {
            try
            {
                poll.Wait(DisposeJoinTimeout);
            }
            catch (AggregateException)
            {
                // The loop already logged its own failure; disposal stays
                // best-effort and never masks it.
            }
        }

        shutdown.Dispose();
    }

    /// <summary>Poll cadence loop. Exits on disposal or on its own failure.</summary>
    private async Task PollAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Report();
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
            _logger.LogWarning(ex, "Process resource sampling stopped after an error");
        }
    }

    private static ProcessResourceSnapshot SampleCurrentProcess()
    {
        using Process process = Process.GetCurrentProcess();
        return new ProcessResourceSnapshot(process.WorkingSet64, process.TotalProcessorTime);
    }
}
