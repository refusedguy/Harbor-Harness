namespace Harbor.Ui.Framework.Rendering.PerformanceContracts;

/// <summary>
///     Formal per-backend renderer performance contract
///     (renderer-unification sprint Phase 6.1).
/// </summary>
/// <remarks>
///     <para>
///         Every renderer backend must declare (and meet) three ceilings:
///     </para>
///     <list type="number">
///         <item><description><see cref="Throughput"/> — minimum streamed
///             events per second for a 1000-token stream on a 24×80 surface;</description></item>
///         <item><description><see cref="Latency"/> — p99 render latency per
///             single event;</description></item>
///         <item><description><see cref="Memory"/> — allocation ceiling per
///             1000 streamed events (steady-state streaming must not balloon
///             the heap).</description></item>
///     </list>
///     <para>
///         Adding a new renderer backend REQUIRES declaring its contract here
///         (or in its own assembly via <see cref="RendererPerformanceContract"/>)
///         and passing <c>tests/Harbor.Tui.PerfTests</c> — enforced by the
///         <c>renderer-perf-gate</c> CI job and the PR-template check.
///     </para>
/// </remarks>
public sealed record RendererPerformanceContract(
    string BackendId,
    ThroughputContract Throughput,
    LatencyContract Latency,
    MemoryContract Memory)
{
    /// <summary>Contracts of the backends wired into the default CLI build.</summary>
    public static IReadOnlyList<RendererPerformanceContract> Defaults { get; } =
    [
        new("ansi",
            new ThroughputContract(MinimumEventsPerSec: 20_000),
            new LatencyContract(P99Budget: TimeSpan.FromMilliseconds(1)),
            // Perf-gate note (issue #77, PR #146): every RenderAsync folds the
            // event into the DI-shared UiStore so the pipeline can restore the
            // snapshot across renderer swaps. The fold costs ~0.55 MB/1k ev on
            // its own (repo-measured 567.48 KB per 1000 TextDelta,
            // docs/.kilo-docs/sprints/performance/benchmark.md §2), so the
            // ceiling matches cellforge (1.0, same TEA fold path) instead of
            // the pre-store 0.5 budget. CI steady-state is ~0.8 MB/1k ev.
            new MemoryContract(MaxAllocatedMbPerThousandEvents: 1.0)),
        new("plain",
            new ThroughputContract(MinimumEventsPerSec: 25_000),
            new LatencyContract(P99Budget: TimeSpan.FromMilliseconds(1)),
            // Same TEA-fold price as ansi above (shared AnsiPlain pipeline).
            new MemoryContract(MaxAllocatedMbPerThousandEvents: 1.0)),
        new("cellforge",
            new ThroughputContract(MinimumEventsPerSec: 10_000),
            new LatencyContract(P99Budget: TimeSpan.FromMilliseconds(2)),
            new MemoryContract(MaxAllocatedMbPerThousandEvents: 1.0)),
        new("nickconsoleex",
            new ThroughputContract(MinimumEventsPerSec: 2_000),
            new LatencyContract(P99Budget: TimeSpan.FromMilliseconds(10)),
            new MemoryContract(MaxAllocatedMbPerThousandEvents: 4.0)),
    ];
}

/// <summary>Minimum sustained event throughput.</summary>
public sealed record ThroughputContract(int MinimumEventsPerSec);

/// <summary>Maximum p99 latency for a single event render.</summary>
public sealed record LatencyContract(TimeSpan P99Budget);

/// <summary>Maximum allocated bytes per 1000 streamed events (steady state).</summary>
public sealed record MemoryContract(double MaxAllocatedMbPerThousandEvents);

/// <summary>Measured metrics of one backend under the synthetic load.</summary>
public sealed record RendererBenchmarkResult(
    string BackendId,
    int EventsRendered,
    double EventsPerSec,
    TimeSpan P99Latency,
    double AllocatedMbPerThousandEvents,
    IReadOnlyList<MarkdownPerformanceMeasurement>? ExtraMeasurements = null)
{
    public bool Meets(RendererPerformanceContract contract) =>
        EventsPerSec >= contract.Throughput.MinimumEventsPerSec
        && P99Latency <= contract.Latency.P99Budget
        && AllocatedMbPerThousandEvents <= contract.Memory.MaxAllocatedMbPerThousandEvents;
}
