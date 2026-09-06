namespace Harbor.Tui.PerfTests;

using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Renderer performance contract gate (renderer-unification sprint Phase
///     6.1). Two enforcement levels:
///     <list type="number">
///         <item><description><b>Absolute contract ceilings</b> — always
///             enforced; a backend that misses its declared throughput, p99
///             latency or allocation ceiling fails the build
///             unconditionally.</description></item>
///         <item><description><b>Relative regression check</b> against the
///             committed baseline — active only under
///             <c>HARBOR_PERF_BASELINE_STRICT=1</c>, because shared CI runners
///             have ±20 % throughput noise; on dedicated perf hardware the
///             flag turns the 5 %-regression rule into a hard gate
///             (see .github/workflows/renderer-perf-gate.yml).</description></item>
///     </list>
/// </summary>
[NotInParallel("perf")]
public class RendererBenchmarkTests
{
    public static readonly string BaselineStrictEnv = "HARBOR_PERF_BASELINE_STRICT";

    [Test]
    [Arguments("ansi")]
    [Arguments("plain")]
    [Arguments("cellforge")]
    [Arguments("nickconsoleex")]
    public async Task Backend_MeetsAbsoluteContract(string backendId)
    {
        RendererPerformanceContract? contract = RendererPerformanceContract.Defaults
            .FirstOrDefault(c => c.BackendId == backendId);
        await Assert.That(contract).IsNotNull();

        RendererBenchmarkResult result = (await RendererBenchmarkSuite.RunAsync(backendId)).Single();

        // Always print the measured metrics — the CI job greps them into
        // BENCHMARKS_RENDERERS.md diffs and the run log.
        Console.Out.WriteLine(
            $"[perf] {backendId}: {result.EventsPerSec:F0} ev/s | p99 {result.P99Latency.TotalMilliseconds:F3} ms | {result.AllocatedMbPerThousandEvents:F4} MB/1k ev");

        await Assert.That(result.Meets(contract!)).IsTrue().Because(
            $"{backendId}: {result.EventsPerSec:F0} ev/s (min {contract!.Throughput.MinimumEventsPerSec}), "
            + $"p99 {result.P99Latency.TotalMilliseconds:F3} ms (max {contract.Latency.P99Budget.TotalMilliseconds:F3} ms), "
            + $"{result.AllocatedMbPerThousandEvents:F4} MB/1k ev (max {contract.Memory.MaxAllocatedMbPerThousandEvents} MB) — "
            + "a regression beyond the declared ceilings must not merge");
    }

    [Test]
    public async Task EveryDefaultBackend_IsCoveredByContracts()
    {
        // Inventory guard: each contract must reference a backend the suite
        // can actually run — adding a backend without benchmarks fails here.
        var runnable = (await RendererBenchmarkSuite.RunAllAsync()).Select(static r => r.BackendId).ToHashSet();
        foreach (RendererPerformanceContract contract in RendererPerformanceContract.Defaults)
        {
            await Assert.That(runnable.Contains(contract.BackendId)).IsTrue()
                .Because($"contract for '{contract.BackendId}' has no benchmark harness");
        }
    }

    [Test]
    public async Task MarkdownContract_IsPartOfTheGate()
    {
        IReadOnlyList<MarkdownPerformanceMeasurement> measurements =
            MarkdownRenderPerformanceGate.Validate();

        foreach (MarkdownPerformanceMeasurement measurement in measurements)
        {
            await Assert.That(measurement.WithinBudget).IsTrue()
                .Because($"{measurement.Scenario} took {measurement.Elapsed.TotalMilliseconds:F3} ms — over contract budget");
        }
    }
}
