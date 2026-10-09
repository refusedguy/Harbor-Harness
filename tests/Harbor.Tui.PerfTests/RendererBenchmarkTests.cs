namespace Harbor.Tui.PerfTests;

using System.Diagnostics;
using Harbor.Ui.Framework.Rendering.Markdown;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Renderer performance contract gate (renderer-unification sprint Phase
///     6.1). Three enforcement levels:
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
///     <list type="number" start="3">
///         <item><description><b>Relative tail-render scaling gate</b> —
///             always enforced; <c>t(2N) &lt;= 3 * t(N)</c> measured in the
///             same run, so the runner's speed cancels (the #998 transfer,
///             #1025).</description></item>
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

    /// <summary>
    ///     Growth allowed for a DOUBLED tail-render input, on cost.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Chosen from the shape of the claim, not from a measurement
    ///         (#1025 measured five green perf-gate samples first: doc ratio
    ///         0.99–1.02, iter ratio 1.98–2.08, alloc 1.00 every run). Doubling
    ///         the tail-render iterations doubles the work, so linear is 2.00;
    ///         doubling the frozen doc should cost nothing at all, so its
    ///         linear is 1.00. 3 leaves headroom over both ideals for a
    ///         collection landing inside one leg — while an O(N^2) regression
    ///         lands at 4 and cannot hide underneath it.
    ///     </para>
    ///     <para>
    ///         2 would redden a correct tree whenever one leg took a collection
    ///         the other did not: the #939 shape, a rule that fails on right
    ///         numbers. 8 sits above the quadratic shape the gate exists to
    ///         catch: what #465 rejected.
    ///     </para>
    /// </remarks>
    private const double GrowthLimit = 3.0;

    [Test]
    public async Task TailRender_Scaling_Gate()
    {
        // #1025: the absolute tail-render budget (2 ms for 100 tail renders)
        // reddened PR #1023 (2.458 ms) whose diff touches no renderer code,
        // and PR #1023 merged with that red — the absolute number has no
        // reader. The probe that stood here measured five green perf-gate
        // samples before this gate was built (same renderer code throughout):
        //
        //   doc100 ms:  1.246, 1.007, 0.677, 1.675, 1.656  (2.47x spread —
        //     the absolute column is the wrong metric, the #998 argument)
        //   doc ratio:  1.00, 1.02, 1.00, 0.99, 0.99
        //   iter ratio: 2.00, 2.08, 2.02, 1.98, 1.99
        //   alloc:      1.00 every run (43520 B both legs — deterministic)
        //
        // Both ratios fit stably inside 3x, so the #998 transfer applies:
        // t(2N) <= 3*t(N) measured in the same run, where runner speed
        // cancels. Doc-doubling is the DESIGN guarantee (frozen blocks free,
        // so its linear is 1.00); iter-doubling is the linearity check
        // (twice the renders, so its linear is 2.00).
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
            + $"(doc x{docRatio:F2}, iter x{iterRatio:F2}, alloc x{allocRatio:F2}, limit x{GrowthLimit:F2}, best of {rounds}; "
            + "absolute columns are informational and runner-dependent)");

        // Non-vacuity: the gates below divide by the small operands, so a
        // zero here means "measured nothing" — the #591 shape.
        await Assert.That(docSmallMs).IsGreaterThan(0.0)
            .Because($"the doc100 baseline must be a real measurement, got {docSmallMs:F3} ms.");
        await Assert.That(iterSmallMs).IsGreaterThan(0.0)
            .Because($"the iter100 baseline must be a real measurement, got {iterSmallMs:F3} ms.");
        await Assert.That(docSmallAlloc).IsGreaterThan(0L)
            .Because($"the doc100 allocation baseline must be a real measurement, got {docSmallAlloc} B.");

        // The gates are RATIOS. Doubling the frozen doc must stay free and
        // doubling the tail renders may at most triple the cost; a quadratic
        // regression lands at 4 on either leg.
        await Assert.That(docRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"doc(200)/doc(100) = {docRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{docLargeMs:F3} ms for a 200-block doc versus {docSmallMs:F3} ms for 100 blocks. "
                + "Frozen blocks render once and are then free, so a correct tree sits at 1.00.");
        await Assert.That(iterRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"iter(200)/iter(100) = {iterRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{iterLargeMs:F3} ms for 200 tail renders versus {iterSmallMs:F3} ms for 100. "
                + "Linear is 2.00, so this bound separates linear from quadratic without depending on "
                + "how fast this runner is.");
        await Assert.That(allocRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"alloc(200)/alloc(100) = {allocRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{docLargeAlloc} B for a 200-block doc versus {docSmallAlloc} B for 100. "
                + "Allocations do not move with the runner's speed at all, so this ratio holds on every machine.");
    }

    /// <summary>
    ///     The declared verdicts, as data. A ratio rule is a claim about
    ///     shapes, so the claim is pinned against a table of shapes rather
    ///     than against whatever a runner happens to produce today.
    /// </summary>
    /// <remarks>
    ///     The limit is carried PER ROW rather than read from
    ///     <see cref="GrowthLimit" />, on purpose: the table then pins the
    ///     decision FUNCTION, so retuning the constant cannot silently rewrite
    ///     what the control is asserting. Rows 1 and 2 are the #1025 probe
    ///     anchors, so the function is tied to a real run of this path. Rows
    ///     4 and 5 are the shapes the gate exists to reject. Rows 6 and 7 are
    ///     the #591 shape: a rule weakened far enough to divide by anything
    ///     must not report a pass on a baseline it never measured.
    /// </remarks>
    private static readonly (string Name, double Small, double Large, double Limit, bool Accept)[] DeclaredContract =
    [
        ("#1025 doc doubling: 1.246 -> 1.247", 1.246, 1.247, 3.0, true),
        ("#1025 iter doubling: 1.007 -> 2.094", 1.007, 2.094, 3.0, true),
        ("exactly at the bound: 500 -> 1500", 500.0, 1500.0, 3.0, true),
        ("quadratic: 500 -> 2000", 500.0, 2000.0, 3.0, false),
        ("above the bound but sub-quadratic: 1.0 -> 3.5", 1.0, 3.5, 3.0, false),
        ("zero baseline — nothing was measured", 0.0, 0.0, 3.0, false),
        ("negative baseline — a broken measurement", -1.0, 10.0, 3.0, false),
    ];

    [Test]
    public async Task TheRatioRuleAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredContract
            .Where(row => WithinGrowthLimit(row.Small, row.Large, row.Limit) != row.Accept)
            .Select(row =>
                row.Name + " (expected " + (row.Accept ? "accepted" : "rejected") + ", rule says "
                + (WithinGrowthLimit(row.Small, row.Large, row.Limit) ? "accepted" : "rejected") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the ratio gate is only meaningful while the rule still separates linear from quadratic and still "
                + "refuses to divide by a baseline it did not measure. If a row disagrees, the rule changed and a "
                + "green cost gate no longer means what it says. Mismatches: "
                + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    /// <summary>
    ///     Whether a doubling of the input may cost at most <paramref name="limit" />
    ///     times as much.
    /// </summary>
    /// <remarks>
    ///     The <c>small &gt; 0</c> guard is load-bearing, not defensive
    ///     decoration. Without it a zero baseline gives <c>0/0 = NaN</c> or
    ///     <c>x/0 = +inf</c>, and the comparison happens to reject both — but
    ///     only as an emergent property of IEEE-754 rather than as a decision
    ///     this file records.
    /// </remarks>
    private static bool WithinGrowthLimit(double small, double large, double limit) =>
        small > 0.0 && large <= small * limit;

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
