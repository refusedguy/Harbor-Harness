namespace Harbor.Abstractions.Events;

/// <summary>
///     Read-only queue-age view over an <see cref="IEventBus" /> — the port the
///     telemetry layer polls to export the publish envelope (#47/S2).
/// </summary>
/// <remarks>
///     <para>
///         <b>What is measured:</b> one local publish envelope per event —
///         submission (the monotonic <c>Stopwatch.GetTimestamp()</c> stamp taken
///         on entry to <c>PublishAsync</c>) to fan-out complete. The distribution
///         of that duration is the only thing that separates "100 events drained
///         instantly" from "3 events stuck behind a 200 ms handler"; a queue
///         length alone cannot (#47).
///     </para>
///     <para>
///         <b>Why a port and not the concrete bus:</b> the counters live on
///         <c>Harbor.Registries</c> while the exporter lives in
///         <c>Harbor.Telemetry.Core</c>, and that pair must not reference each
///         other (docs/ARCHITECTURE_LAYERS.md §2 — <c>Harbor.Telemetry.Core</c>
///         may depend on Abstractions + Diagnostics.Abstractions only). The
///         contract therefore lives in the hexagon core: the bus implements it,
///         the telemetry layer consumes it, and the composition root wires the
///         two together. Implementations MUST derive every duration from a
///         monotonic clock and MUST report a bounded (O(1)) sample count — the
///         window never grows with the number of publishes.
///     </para>
///     <para>
///         Implementations that have no instrumentation (a bare
///         <see cref="IEventBus" />, a remote/IPC bus) may report zeroes and an
///         empty window; the exporter treats that as "not measured" rather than
///         "measured zero".
///     </para>
/// </remarks>
public interface IEventBusQueueMetrics
{
    /// <summary>
    ///     Total publishes that entered the queue envelope since construction.
    ///     The zero-subscriber / zero-scrollback / zero-middleware fast path
    ///     never enters it, so this counts publishes that were actually queued.
    /// </summary>
    long PublishedCount { get; }

    /// <summary>
    ///     Publishes currently submitted but not yet fanned out. Zero in the
    ///     quiescent state.
    /// </summary>
    long InflightPublishCount { get; }

    /// <summary>
    ///     Age of the oldest still-pending publish, measured from its
    ///     monotonic submission stamp. <see cref="TimeSpan.Zero" /> once
    ///     <see cref="InflightPublishCount" /> returns to zero.
    /// </summary>
    TimeSpan OldestPendingAge { get; }

    /// <summary>
    ///     Slowest publish envelope observed since construction (monotonic
    ///     max, exact — not quantised). <see cref="DispatchDurationPercentile" />
    ///     gives the distribution view of the same quantity.
    /// </summary>
    TimeSpan MaxDispatchDuration { get; }

    /// <summary>
    ///     Fixed number of dispatch-duration samples the implementation retains
    ///     (its window capacity). Constant for the lifetime of the bus —
    ///     retained memory is O(1) in the number of publishes.
    /// </summary>
    int DispatchSampleCapacity { get; }

    /// <summary>
    ///     Samples currently held in the window: <c>0..DispatchSampleCapacity</c>,
    ///     saturating at the capacity. Independent of how many publishes ran.
    /// </summary>
    int DispatchSampleCount { get; }

    /// <summary>
    ///     Percentile of the retained dispatch-duration window
    ///     (submission → fan-out complete). <paramref name="quantile" /> is
    ///     clamped to <c>[0, 1]</c> (0 = fastest retained sample, 1 = slowest);
    ///     returns <see cref="TimeSpan.Zero" /> for an empty window.
    /// </summary>
    /// <param name="quantile">Requested quantile, e.g. 0.50 / 0.95 / 0.99.</param>
    TimeSpan DispatchDurationPercentile(double quantile);
}
