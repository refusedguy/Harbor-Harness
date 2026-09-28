using Harbor.Abstractions.Events;
using Harbor.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Harbor.Telemetry;

/// <summary>
///     Exports the event-bus publish envelope to Harbor's metrics surface
///     (#47/S2) — the p50/p95/p99/max distribution of completed publishes plus
///     the live oldest-pending age.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the distribution and not the max.</b> #47's core observation:
///         <i>queue length alone lies</i> — 100 fast-draining events are fine, 3
///         events behind a 200 ms handler are not, and a single monotonic max
///         cannot tell those two worlds apart. Percentiles over
///         submission → fan-out-complete duration can, and they are the only way
///         to check whether later fast-path work actually improved latency.
///     </para>
///     <para>
///         <b>How it is wired.</b> The bus (<c>Harbor.Registries</c>) and this
///         reporter (<c>Harbor.Telemetry.Core</c>) may not reference each other
///         (docs/ARCHITECTURE_LAYERS.md §2), so both sides meet on the
///         <see cref="IEventBusQueueMetrics" /> port in
///         <c>Harbor.Abstractions</c> and the composition root hands the bus
///         instance to this reporter. No new packages, and no
///         <c>IMetrics</c> dependency inside <c>Harbor.Registries</c>.
///     </para>
///     <para>
///         <b>Instruments</b> — stable names on
///         <see cref="HarborTelemetrySources.Instruments" />, so every
///         <c>MeterListener</c> and the OTLP exporter
///         (<c>Harbor.Telemetry.Otlp</c>) pick them up:
///         <list type="table">
///             <item>
///                 <term><c>eventbus.dispatch.duration.ms</c></term>
///                 <description>histogram tagged <c>dispatch.quantile</c> ∈ p50 / p95 / p99 / max — the completed-publish envelope distribution.</description>
///             </item>
///             <item>
///                 <term><c>eventbus.queue.oldest.pending.age.ms</c></term>
///                 <description>age of the oldest still-pending publish (monotonic stamp; 0 once inflight drains).</description>
///             </item>
///             <item>
///                 <term><c>eventbus.publish.inflight</c></term>
///                 <description>publishes submitted but not yet fanned out.</description>
///             </item>
///             <item>
///                 <term><c>eventbus.publish.count</c></term>
///                 <description>counter, advanced by the delta of queued publishes since the previous report.</description>
///             </item>
///         </list>
///         Each report also writes one Debug line, so <c>harbor logs --last</c>
///         shows the numbers with no debugger and no OTLP endpoint attached.
///     </para>
/// </remarks>
public sealed class EventBusQueueAgeReporter : IDisposable
{
    /// <summary>Default poll cadence used by the CLI/desktop presets.</summary>
    public static readonly TimeSpan DefaultReportInterval = TimeSpan.FromSeconds(30);

    /// <summary>Upper clamp for the poll cadence (<c>PeriodicTimer</c> is uint-milliseconds).</summary>
    private static readonly TimeSpan MaxReportInterval = TimeSpan.FromDays(1);

    /// <summary>Bound on the Dispose join — the loop only waits for a tick.</summary>
    private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(1);

    private readonly IMetrics _metrics;
    private readonly ILogger _logger;
    private readonly IEventBusQueueMetrics _source;

    private CancellationTokenSource? _shutdown;
    private Task? _pollTask;
    private long _lastPublishedCount;
    private bool _disposed;

    /// <summary>
    ///     Create a reporter over one bus. When <paramref name="pollInterval" />
    ///     is a positive <see cref="TimeSpan" /> the reporter starts its own
    ///     cadence; pass <c>null</c> for a host that calls
    ///     <see cref="Report" /> itself (tests, one-shot diagnostics).
    /// </summary>
    /// <param name="source">Queue-age view of the bus to export.</param>
    /// <param name="metrics">Instrument sink — normally <see cref="MeterMetrics.Instance" />.</param>
    /// <param name="logger">Category logger; the snapshot line is written at Debug (always enabled in the per-run log file).</param>
    /// <param name="pollInterval">Cadence for the background report loop; <c>null</c> or non-positive disables it.</param>
    public EventBusQueueAgeReporter(
        IEventBusQueueMetrics source,
        IMetrics metrics,
        ILogger logger,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _metrics = metrics;
        _logger = logger;

        // Baseline so the first counter delta only covers publishes observed
        // after wiring, not the bus's whole lifetime before it.
        _lastPublishedCount = source.PublishedCount;

        if (pollInterval is { } requested && requested > TimeSpan.Zero)
        {
            TimeSpan interval = requested < MaxReportInterval ? requested : MaxReportInterval;
            _shutdown = new CancellationTokenSource();
            CancellationToken token = _shutdown.Token;
            _pollTask = Task.Run(() => PollAsync(interval, token), CancellationToken.None);
        }
    }

    /// <summary>
    ///     Emit one queue-age snapshot: the p50/p95/p99/max envelope distribution,
    ///     the live oldest-pending age, the inflight count, the publish delta, and
    ///     one Debug log line. Safe to call repeatedly; not re-entrant (the
    ///     background loop is the only caller in hosted processes).
    /// </summary>
    public void Report()
    {
        double p50 = _source.DispatchDurationPercentile(0.50).TotalMilliseconds;
        double p95 = _source.DispatchDurationPercentile(0.95).TotalMilliseconds;
        double p99 = _source.DispatchDurationPercentile(0.99).TotalMilliseconds;
        double max = _source.MaxDispatchDuration.TotalMilliseconds;
        double oldestPendingMs = _source.OldestPendingAge.TotalMilliseconds;
        long inflight = _source.InflightPublishCount;
        long published = _source.PublishedCount;

        // Nearest-rank percentiles of one envelope → one instrument, four
        // dimensions (same shape as the llm.tokens counter + token.type tag).
        _metrics.Histogram(TelemetryTagNames.EventBusDispatchDurationMs, p50, Quantile("p50"));
        _metrics.Histogram(TelemetryTagNames.EventBusDispatchDurationMs, p95, Quantile("p95"));
        _metrics.Histogram(TelemetryTagNames.EventBusDispatchDurationMs, p99, Quantile("p99"));
        _metrics.Histogram(TelemetryTagNames.EventBusDispatchDurationMs, max, Quantile("max"));
        _metrics.Histogram(TelemetryTagNames.EventBusOldestPendingAgeMs, oldestPendingMs);
        _metrics.Histogram(TelemetryTagNames.EventBusPublishInflight, inflight);

        long delta = published - Interlocked.Exchange(ref _lastPublishedCount, published);
        if (delta > 0)
        {
            _metrics.Counter(TelemetryTagNames.EventBusPublishCount, delta);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "eventbus queue age: dispatch p50={P50:F3}ms p95={P95:F3}ms p99={P99:F3}ms max={Max:F3}ms | oldest-pending={OldestPendingMs:F3}ms inflight={Inflight} published={Published} samples={Samples}/{SampleCapacity}",
                p50, p95, p99, max, oldestPendingMs, inflight, published,
                _source.DispatchSampleCount, _source.DispatchSampleCapacity);
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
            _logger.LogWarning(ex, "Event-bus queue-age reporting stopped after an error");
        }
    }

    private static KeyValuePair<string, object?> Quantile(string name) =>
        new(TelemetryTagNames.DispatchQuantile, name);
}
