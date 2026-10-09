namespace Harbor.Tui.PerfTests;

using System.Diagnostics;
using Harbor.Ui.Framework.Rendering.Markdown;
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
[NotInParallel("pty")]
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

    [Test]
    public async Task TailRender_Scaling_Probe()
    {
        // #1025 probe — prints, does NOT gate. The absolute tail-render budget
        // (2 ms for 100 tail renders) reddened PR #1023 (2.458 ms) whose diff
        // touches no renderer code, and PR #1023 merged with that red, so the
        // absolute number has no reader either. Before a #998-style relative
        // gate (t(2N) <= 3*t(N), same run) is built, this measures the paired
        // ratios it would assert: doc-size doubling (100- vs 200-block doc,
        // the DESIGN guarantee — frozen blocks free) and iteration doubling
        // (100 vs 200 tail renders, the #998 transfer). Best of 3 rounds, same
        // run, so runner speed cancels. If either ratio's spread across green
        // runs does not fit in 3x, the relative gate flakes the same way and
        // must NOT be built — decision goes to the owner on #1025.
        const int rounds = 3;
        double docSmallMs = double.MaxValue;
        double docLargeMs = double.MaxValue;
        double iterSmallMs = double.MaxValue;
        double iterLargeMs = double.MaxValue;
        long docSmallAlloc = long.MaxValue;
        long docLargeAlloc = long.MaxValue;
        for (int round = 0; round < rounds; round++)
        {
            (double t100, long a100) = MeasureTailRender(frozenBlocks: 99, iterations: 100);
            (double tDoc200, long aDoc200) = MeasureTailRender(frozenBlocks: 199, iterations: 100);
            (double tIter200, long _) = MeasureTailRender(frozenBlocks: 99, iterations: 200);
            docSmallMs = Math.Min(docSmallMs, t100);
            docLargeMs = Math.Min(docLargeMs, tDoc200);
            iterSmallMs = Math.Min(iterSmallMs, t100);
            iterLargeMs = Math.Min(iterLargeMs, tIter200);
            docSmallAlloc = Math.Min(docSmallAlloc, a100);
            docLargeAlloc = Math.Min(docLargeAlloc, aDoc200);
        }

        double docRatio = docLargeMs / docSmallMs;
        double iterRatio = iterLargeMs / iterSmallMs;
        double allocRatio = (double)docLargeAlloc / docSmallAlloc;
        Console.WriteLine(
            $"tail-render-scaling: doc100 = {docSmallMs:F3} ms / {docSmallAlloc} B, "
            + $"doc200 = {docLargeMs:F3} ms / {docLargeAlloc} B "
            + $"(doc x{docRatio:F2}, iter x{iterRatio:F2}, alloc x{allocRatio:F2}, best of {rounds}; "
            + "absolute columns are informational and runner-dependent)");

        // Non-vacuity only: the future gate divides by the small operands, so
        // a zero here means "measured nothing" — the #591 shape. No ratio is
        // asserted until the spread across green runs is known (#1025).
        await Assert.That(docSmallMs).IsGreaterThan(0.0)
            .Because($"the doc100 baseline must be a real measurement, got {docSmallMs:F3} ms.");
        await Assert.That(docSmallAlloc).IsGreaterThan(0L)
            .Because($"the doc100 allocation baseline must be a real measurement, got {docSmallAlloc} B.");
    }

    private static (double Ms, long Allocated) MeasureTailRender(int frozenBlocks, int iterations)
    {
        // Same setup as MarkdownRenderPerformanceGate scenario 2 (tail-only
        // re-render), parametrised by frozen doc size and loop count.
        var pipeline = new DifferentialMarkdownPipeline(cols: 80, rows: 24);
        var oneRow = new List<MdLine> { new([new MdSpan("frozen block", MdStyle.Normal)]) };
        for (int i = 0; i < frozenBlocks; i++)
        {
            _ = pipeline.RenderBlock(i, oneRow, isComplete: true, y: 0);
        }

        int tailId = frozenBlocks;
        var tail = new MdLine([new MdSpan("tail text ", MdStyle.Normal)]);
        for (int i = 0; i < 100; i++)
        {
            _ = pipeline.RenderBlock(tailId, [tail, new MdLine([new MdSpan("warm", MdStyle.Normal)])], isComplete: false, y: 0);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int token = 0; token < iterations; token++)
        {
            _ = pipeline.RenderBlock(tailId, [tail, new MdLine([new MdSpan($"token {token}", MdStyle.Normal)])], isComplete: false, y: 0);
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(pipeline);
        return ((double)elapsedTicks / Stopwatch.Frequency * 1000.0, allocated);
    }
}
