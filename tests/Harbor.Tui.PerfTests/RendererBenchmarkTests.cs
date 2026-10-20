namespace Harbor.Tui.PerfTests;

using System.Diagnostics;
using Harbor.Ui.Framework.Rendering.Markdown;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using Harbor.TestKit;
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

        // The other direction: a backend the suite runs must declare a
        // contract. Without this half the guard only caught a contract with
        // no benchmark — a benchmark with no contract (a backend that is
        // measured but whose ceilings nobody enforces) passed silently, and
        // that is exactly the shape a removed backend leaves behind when
        // only half of its two rows are deleted.
        var contracted = RendererPerformanceContract.Defaults
            .Select(static c => c.BackendId).ToHashSet(StringComparer.Ordinal);
        foreach (string backendId in runnable)
        {
            await Assert.That(contracted.Contains(backendId)).IsTrue()
                .Because($"backend '{backendId}' is benchmarked but declares no performance contract, "
                       + "so its throughput/latency/allocation ceilings are not enforced by the gate");
        }
    }

    [Test]
    public async Task MarkdownContract_IsPartOfTheGate()
    {
        IReadOnlyList<MarkdownPerformanceMeasurement> measurements =
            MarkdownRenderPerformanceGate.Validate();

        foreach (MarkdownPerformanceMeasurement measurement in measurements)
        {
            if (measurement.Scenario == "tail-render-100-blocks")
            {
                // #1304: the absolute tail-render column is informational, not
                // a gate — same-run noise on shared runners spans ~3x on
                // unchanged code (#1025 probe: 0.677–1.675 ms; #1304 hits at
                // 2.041–2.083 ms vs a 2.0 ms budget), so no threshold
                // separates signal from noise (the #996 argument). Enforcement
                // lives in TailRender_Scaling_Gate below, where runner speed
                // cancels.
                Console.WriteLine(
                    $"[perf] {measurement.Scenario}: {measurement.Elapsed.TotalMilliseconds:F3} ms "
                    + "(informational — gated relatively by TailRender_Scaling_Gate, #1304)");
                continue;
            }

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
        //
        // #1652 measure: the SHARED Harbor.TestKit's TestScalingGate (since
        // #1889 this project references the shared file instead of keeping a
        // local copy — the TestKit core no longer drags Harbor.Application
        // into the slice this project measures) runs the legs GC-fenced,
        // scaled to
        // ~100 ms of real work (the millisecond-leg best-of shape is where
        // the noise lives), and reports MEDIANS over five rounds. The old
        // hand-rolled loop shared one doc-100 leg between the two ratio
        // claims; two leg pairs measure the two claims independently and
        // the alloc claim rides the doc pair.
        TestScalingGateResult docResult = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(AsLeg(MeasureTailRender(frozenBlocks: 99, iterations: units))),
            measureLargeLeg: units => Task.FromResult(AsLeg(MeasureTailRender(frozenBlocks: 199, iterations: units))),
            initialUnits: 100,
            options: new TestScalingGateOptions
            {
                Label = "tail-render-doc-scaling",
                GrowthLimit = GrowthLimit,
            });

        await docResult.AssertWithinLimitsAsync(
            topic: "doc(200)/doc(100)",
            linearWhy: "Doubling the frozen doc should cost nothing at all (frozen blocks render once and are then free, so a correct tree sits at 1.00); a quadratic regression lands at 4.");

        TestScalingGateResult iterResult = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(AsLeg(MeasureTailRender(frozenBlocks: 99, iterations: units))),
            measureLargeLeg: units => Task.FromResult(AsLeg(MeasureTailRender(frozenBlocks: 99, iterations: units * 2))),
            initialUnits: 100,
            options: new TestScalingGateOptions
            {
                Label = "tail-render-iter-scaling",
                GrowthLimit = GrowthLimit,
                MeasureAllocations = false,
            });

        await iterResult.AssertWithinLimitsAsync(
            topic: "iter(200)/iter(100)",
            linearWhy: "Twice the tail renders is twice the work (linear is 2.00), so this bound separates linear from quadratic without depending on how fast this runner is.");
    }

    private static TestScalingGateLeg AsLeg((double Ms, long Allocated) leg) => new(leg.Ms, leg.Allocated);

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
            .Where(row => TestScalingGate.WithinLimit(row.Small, row.Large, row.Limit) != row.Accept)
            .Select(row =>
                row.Name + " (expected " + (row.Accept ? "accepted" : "rejected") + ", rule says "
                + (TestScalingGate.WithinLimit(row.Small, row.Large, row.Limit) ? "accepted" : "rejected") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the ratio gate is only meaningful while the rule still separates linear from quadratic and still "
                + "refuses to divide by a baseline it did not measure. If a row disagrees, the rule changed and a "
                + "green cost gate no longer means what it says. Mismatches: "
                + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
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
