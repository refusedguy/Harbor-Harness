// PerfGate.cs — #1747: the SHARED "measure → report → assert softly" helper
// behind every performance gate that is NOT a relative N-vs-2N ratio.
//
// WHY A SECOND HELPER, WHEN TestScalingGate ALREADY EXISTS
// ---------------------------------------------------------
// `TestScalingGate` (#1652) answers ONE question: "does doubling the input
// cost at most 3x?" Its answer is a RATIO, and a ratio has two properties
// that make it a poor merge gate on a shared runner:
//
//   * the DENOMINATOR is the same order of magnitude as the numerator, so
//     runner noise moves the ratio a long way — the measured receipts are
//     `SqliteAppendScalingGate` red at "ratio 3.102 vs limit 3.00" (3% over
//     on a loaded runner) and `ProjectHistoryScalingGates` red at "time x3.10
//     while alloc x1.99 on a byte-identical tree" (#1285);
//   * the BASE is not a fixed amount of work, so the number the gate reads
//     depends on the size the gate happened to pick.
//
// THIS HELPER ANSWERS A DIFFERENT QUESTION: "how much does ONE UNIT OF WORK
// cost, and is that within a reviewed constant?" The amount of work is FIXED
// by the caller (`units`), the budget is a constant PER UNIT, and the two
// branches are scaled differently:
//
//   * ALLOCATIONS are machine-independent. A byte count does not move with the
//     runner's speed, so `MaxBytesPerUnit` is compared as-is, with no band.
//     This is the branch that can be a hard gate on any machine.
//   * TIME is machine-dependent, so it is expressed in PROBE UNITS — the cost
//     of one unit of work divided by the cost of one unit of the fixed
//     calibration probe measured on the SAME runner in the SAME run. The
//     runner's speed appears in both operands and cancels, so the budget is a
//     constant in a machine-independent currency rather than a number of
//     milliseconds that only means something on the box that produced it.
//
// THE THREE CONTRACT POINTS
// --------------------------
//   1. MEASURE FIRST. The workload is measured before anything is asserted;
//      the report line is printed BEFORE the assertions run, so a red gate
//      leaves the measurement that produced it in the log (and in the job
//      summary, via tools/test-measurements.py — the #618 mechanism).
//   2. REPORT ALWAYS. The report is printed on every run, pass or fail, and
//      it says "measured" explicitly. A gate that measures but does not fail
//      is still evidence; a gate that fails without leaving a number behind
//      is just a red light.
//   3. ASSERT SOFTLY. The allocation branch asserts hard. The time branch
//      asserts ONLY when the measurement is TRUSTED — the spread across the
//      measured rounds is within `MaxTrustedSpread`. A runner that cannot
//      reproduce its own baseline is not evidence about the code, so the time
//      branch reports and stands down instead of failing the build (the
//      `StatusBarFitPerfTests` #487 precedent: "calibrate the harness before
//      believing it"). Non-vacuity is asserted either way: a leg that
//      measured nothing is a failure, not a pass.
//
// WHO MAY REFERENCE THIS
// ----------------------
// Harbor.TestKit is the master copy. Projects that must not drag
// Harbor.Application in (the cost-gate projects the TestScalingGate header
// lists) keep a DOCUMENTED SAME-API INTERNAL COPY of this file
// (grep "PerfGate.cs — #1747" under tests/); keep the copies in sync.

using TUnit.Assertions;

namespace Harbor.TestKit;

/// <summary>
///     One measured sample of a perf gate: the wall time of the window and the
///     bytes allocated inside it.
/// </summary>
/// <remarks>
///     <see cref="Allocated" /> is whatever probe the caller chose — the
///     per-thread counter for a synchronous window
///     (<see cref="AllocationProbe.MeasureThread" />), the process-wide one
///     for a window that can await
///     (<see cref="AllocationProbe.MeasureProcessAsync(Func{Task})" />). A
///     time-only gate may pass <c>0</c>.
/// </remarks>
public readonly record struct PerfGateLeg(double Ms, long Allocated);

/// <summary>Tuning knobs for <see cref="PerfGate.MeasureAsync" />.</summary>
public sealed record PerfGateOptions
{
    /// <summary>Log-facing name of the gate, printed on the deciding line.</summary>
    public string Label { get; init; } = "perf-gate";

    /// <summary>
    ///     Allocation budget for ONE unit of work, in bytes. Machine-independent:
    ///     it is compared as measured, with no band and no scaling. This is the
    ///     branch that can be a hard gate on any runner.
    /// </summary>
    public long MaxBytesPerUnit { get; init; } = long.MaxValue;

    /// <summary>
    ///     Time budget for ONE unit of work, in PROBE UNITS (see
    ///     <see cref="PerfGate" />). Machine-independent by construction: one
    ///     probe unit is one mix of the fixed calibration workload, measured on
    ///     the same runner in the same run. <see cref="double.PositiveInfinity" />
    ///     (the default) means the time branch is measured and reported but not
    ///     asserted — the shape a gate takes when its cost is dominated by
    ///     something no CPU yardstick scales (a storage stack's fsync, a
    ///     network round trip).
    /// </summary>
    public double MaxProbeUnitsPerUnit { get; init; } = double.PositiveInfinity;

    /// <summary>
    ///     Measured rounds. Five: the leg is a repeat of the same work, so
    ///     rounds are cheap, and a median needs headroom over noise.
    /// </summary>
    public int Rounds { get; init; } = 5;

    /// <summary>
    ///     Warm-up rounds, measured and discarded. They pay the tiered-JIT
    ///     compilation of the measured path, which would otherwise land on the
    ///     first measured round and inflate it.
    /// </summary>
    public int WarmupRounds { get; init; } = 1;

    /// <summary>
    ///     Multiplier applied to the time budget — the variance headroom a
    ///     shared runner needs. It applies to TIME only: the allocation budget
    ///     carries no band, because a byte count does not drift with the
    ///     runner.
    /// </summary>
    public double VarianceHeadroom { get; init; } = 1.25;

    /// <summary>
    ///     Above this relative spread across the measured rounds the
    ///     measurement is NOT trusted, and the time branch reports instead of
    ///     failing. A runner that cannot reproduce its own baseline is not
    ///     evidence about the code.
    /// </summary>
    public double MaxTrustedSpread { get; init; } = 1.5;

    /// <summary>
    ///     Below this median leg duration the time measurement is not trusted:
    ///     a leg shorter than a few milliseconds decides nothing on a hosted
    ///     runner. The allocation branch is unaffected — a byte counter is exact
    ///     whatever the leg's length.
    /// </summary>
    public double MinMeasurableLegMs { get; init; } = 5.0;

    /// <summary>
    ///     Whether the allocation branch must have measured a POSITIVE number
    ///     of bytes per unit. True by default: a byte count of zero satisfies
    ///     every ceiling, so a mis-wired probe would make the gate pass on any
    ///     tree (the #591 shape). Set it to <c>false</c> for a workload that
    ///     legitimately allocates nothing per unit — an allocation-free fast
    ///     path — where zero is the correct answer and not a missing
    ///     measurement.
    /// </summary>
    public bool RequirePositiveBytes { get; init; } = true;
}

/// <summary>
///     The outcome of <see cref="PerfGate.MeasureAsync" />: the measured
///     per-unit costs, the derived verdicts, and the assert surface.
/// </summary>
public sealed record PerfGateResult
{
    public required string Label { get; init; }
    public required int Units { get; init; }
    public required int Rounds { get; init; }

    /// <summary>Median wall time of one unit of work, in milliseconds.</summary>
    public required double MsPerUnit { get; init; }

    /// <summary>Median allocation of one unit of work, in bytes.</summary>
    public required double BytesPerUnit { get; init; }

    /// <summary>Median cost of one probe unit on this runner, in milliseconds.</summary>
    public required double ProbeMsPerUnit { get; init; }

    /// <summary>
    ///     How many probe units one unit of work cost. The machine-independent
    ///     currency the time budget is written in.
    /// </summary>
    public required double ProbeUnitsPerWorkUnit { get; init; }

    /// <summary>Relative spread of the measured rounds (max / min).</summary>
    public required double RelativeSpread { get; init; }

    /// <summary>Whether the measurement may be asserted against a budget.</summary>
    public required bool Trusted { get; init; }

    public required long MaxBytesPerUnit { get; init; }
    public required double MaxProbeUnitsPerUnit { get; init; }

    /// <summary>
    ///     Whether the allocation branch must have measured a POSITIVE number
    ///     of bytes per unit (see
    ///     <see cref="PerfGateOptions.RequirePositiveBytes" />).
    /// </summary>
    public required bool RequirePositiveBytes { get; init; }

    /// <summary>Whether the allocation branch is within its budget.</summary>
    public required bool BytesWithinBudget { get; init; }

    /// <summary>Whether the time branch is within its budget.</summary>
    public required bool TimeWithinBudget { get; init; }

    /// <summary>
    ///     Whether the time branch is asserted at all. False when no time
    ///     budget was declared, or when the measurement was not trusted.
    /// </summary>
    public required bool TimeAsserted { get; init; }

    /// <summary>Why the time branch stood down, when it did.</summary>
    public required string? TimeStandDownReason { get; init; }

    /// <summary>
    ///     The measurement, printed on every run. The word "measured" is in it
    ///     on purpose: a gate that measures and reports without failing is
    ///     still evidence, and this line is what the job summary lifts.
    /// </summary>
    public string Report
    {
        get
        {
            string bytes = $"bytes {BytesPerUnit:F1} B/unit";
            string time = $"time {MsPerUnit:F4} ms/unit = {ProbeUnitsPerWorkUnit:F1} probe units/unit";
            string verdict = BytesWithinBudget ? "bytes PASS" : "bytes FAIL";
            verdict += TimeAsserted
                ? (TimeWithinBudget ? ", time PASS" : ", time FAIL")
                : ", time REPORTED ONLY";

            string bytesBudget = MaxBytesPerUnit == long.MaxValue
                ? "no byte budget"
                : $"≤ {MaxBytesPerUnit} B/unit";
            string timeBudget = double.IsInfinity(MaxProbeUnitsPerUnit)
                ? "no time budget"
                : $"≤ {MaxProbeUnitsPerUnit:F1} probe units/unit";

            return $"[perf-gate] {Label}: measured {Units} unit(s) over {Rounds} round(s) — "
                + $"{time}, {bytes} | budget: {bytesBudget}, {timeBudget} "
                + $"| probe {ProbeMsPerUnit:F6} ms/unit, spread {RelativeSpread:F2}x, "
                + $"{(Trusted ? "trusted" : "NOT trusted")} | verdict: {verdict}";
        }
    }

    /// <summary>
    ///     Asserts the budgets, in the order the discipline demands: the
    ///     measurement is real, the machine-independent allocation branch is
    ///     hard, and the machine-dependent time branch is asserted only when
    ///     the measurement is trusted.
    /// </summary>
    /// <param name="topic">
    ///     What the gate is about, in the test's own words — e.g.
    ///     "sqlite append per message".
    /// </param>
    public async Task AssertWithinBudgetsAsync(string topic)
    {
        // Non-vacuity first: a gate that measured nothing must not read as a
        // pass. This is the #591 shape.
        await Assert.That(MsPerUnit).IsGreaterThan(0.0)
            .Because(
                $"the measurement must be real ({topic}, {Label}): {MsPerUnit:F4} ms per unit over "
                + $"{Units} unit(s) x {Rounds} round(s). A zero there means nothing was measured, and a "
                + "gate that cannot tell 'no cost' from 'no measurement' accepts any tree.");

        if (MaxBytesPerUnit != long.MaxValue)
        {
            if (RequirePositiveBytes)
            {
                await Assert.That(BytesPerUnit).IsGreaterThan(0.0)
                    .Because(
                        $"the allocation baseline must be a real measurement ({topic}, {Label}): "
                        + $"{BytesPerUnit:F1} B per unit. A zero denominator makes the byte budget meaningless.");
            }
            else
            {
                // A workload that legitimately allocates nothing per unit: zero
                // is the answer, not a missing measurement. The floor is still
                // there — a negative byte count describes nothing (#741).
                await Assert.That(BytesPerUnit).IsGreaterThanOrEqualTo(0.0)
                    .Because(
                        $"a negative allocation measurement describes nothing ({topic}, {Label}): "
                        + $"{BytesPerUnit:F1} B per unit.");
            }

            await Assert.That(BytesPerUnit).IsLessThanOrEqualTo((double)MaxBytesPerUnit)
                .Because(
                    $"{topic}: {BytesPerUnit:F1} B per unit against a budget of {MaxBytesPerUnit} B per unit. "
                    + "Allocations do not move with the runner's speed, so this budget holds on every machine "
                    + "— it is the branch that can be a hard gate.");
        }

        if (!TimeAsserted)
        {
            // The soft branch. Measured, reported, not failed.
            Console.WriteLine(
                $"[perf-gate] {Label}: time branch REPORTED ONLY — {TimeStandDownReason} "
                + $"(measured {ProbeUnitsPerWorkUnit:F1} probe units/unit against a budget of "
                + $"{MaxProbeUnitsPerUnit:F1}).");
            return;
        }

        await Assert.That(ProbeUnitsPerWorkUnit).IsLessThanOrEqualTo(MaxProbeUnitsPerUnit)
            .Because(
                $"{topic}: {ProbeUnitsPerWorkUnit:F1} probe units per unit against a budget of "
                + $"{MaxProbeUnitsPerUnit:F1} probe units per unit — measured {MsPerUnit:F4} ms/unit against "
                + $"a probe that costs {ProbeMsPerUnit:F6} ms/unit on this runner (spread {RelativeSpread:F2}x "
                + $"over {Rounds} rounds, so the measurement was trusted). The budget is in probe units because "
                + "an absolute millisecond budget measures the runner, not the code.");
    }
}

/// <summary>
///     The shared measure-and-report helper behind every per-unit-of-work
///     performance gate (#1747). See the file header for what it replaces and
///     why.
/// </summary>
public static class PerfGate
{
    /// <summary>Integer operations in one probe mix.</summary>
    public const int ProbeMixSteps = 64;

    /// <summary>Mixes in one probe sample.</summary>
    public const int ProbeSampleMixes = 40_000;

    /// <summary>Probe samples taken per gate; the median is the yardstick.</summary>
    public const int ProbeSamples = 3;

    /// <summary>
    ///     Measures a workload and reports it, without asserting anything.
    /// </summary>
    /// <param name="work">
    ///     Runs <c>units</c> units of work and reports the window's wall time
    ///     and its allocation. It is called once per warm-up round and once per
    ///     measured round, so it must be REPEATABLE: a gate that appends to a
    ///     store has to start from a fresh session on every call, or the later
    ///     rounds measure a different amount of work than the first.
    /// </param>
    /// <param name="units">Units of work per measured leg.</param>
    /// <param name="options">Tuning; the defaults carry the #1747 recipe.</param>
    public static async Task<PerfGateResult> MeasureAsync(
        Func<int, Task<PerfGateLeg>> work,
        int units,
        PerfGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        PerfGateOptions opts = options ?? new PerfGateOptions();
        if (opts.Rounds < 3)
        {
            throw new InvalidOperationException(
                $"[PerfGate] {opts.Label}: Rounds = {opts.Rounds} is below the median's minimum of 3.");
        }

        if (units <= 0)
        {
            throw new InvalidOperationException(
                $"[PerfGate] {opts.Label}: units = {units}. A gate that measured no work decides nothing.");
        }

        // Warm-up: paid, discarded, and fenced like every other round, so the
        // tiered-JIT compilation it triggers does not land on the first
        // measured round.
        for (int warmup = 0; warmup < opts.WarmupRounds; warmup++)
        {
            Fence();
            _ = await work(units).ConfigureAwait(false);
        }

        double[] ms = new double[opts.Rounds];
        long[] allocated = new long[opts.Rounds];
        for (int round = 0; round < opts.Rounds; round++)
        {
            Fence();
            PerfGateLeg leg = await work(units).ConfigureAwait(false);
            ms[round] = leg.Ms;
            allocated[round] = leg.Allocated;
        }

        double medianMs = Median(ms);
        long medianAllocated = Median(allocated);
        double spread = Spread(ms);

        // The yardstick: this runner's speed, measured on this runner, in this
        // run. The budget is written in probe units, so the runner's speed
        // appears in both operands and cancels.
        double probeMsPerUnit = MeasureProbeMsPerUnit();

        double msPerUnit = medianMs / units;
        double bytesPerUnit = (double)medianAllocated / units;
        double probeUnitsPerWorkUnit = probeMsPerUnit > 0.0 ? msPerUnit / probeMsPerUnit : 0.0;

        bool trusted = spread <= opts.MaxTrustedSpread && medianMs >= opts.MinMeasurableLegMs;
        bool bytesBudgeted = opts.MaxBytesPerUnit != long.MaxValue;
        bool timeBudgeted = !double.IsInfinity(opts.MaxProbeUnitsPerUnit);

        bool bytesWithin = !bytesBudgeted || bytesPerUnit <= opts.MaxBytesPerUnit;
        bool timeWithin = !timeBudgeted || probeUnitsPerWorkUnit <= opts.MaxProbeUnitsPerUnit;

        string? standDown = null;
        if (!timeBudgeted)
        {
            standDown = "no time budget declared (the cost is not scaled by a CPU yardstick)";
        }
        else if (!trusted)
        {
            standDown = $"the measurement was not trusted (spread {spread:F2}x over {opts.Rounds} rounds, "
                + $"median leg {medianMs:F4} ms, needs spread <= {opts.MaxTrustedSpread:F2}x "
                + $"and a leg >= {opts.MinMeasurableLegMs:F0} ms)";
        }

        var result = new PerfGateResult
        {
            Label = opts.Label,
            Units = units,
            Rounds = opts.Rounds,
            MsPerUnit = msPerUnit,
            BytesPerUnit = bytesPerUnit,
            ProbeMsPerUnit = probeMsPerUnit,
            ProbeUnitsPerWorkUnit = probeUnitsPerWorkUnit,
            RelativeSpread = spread,
            Trusted = trusted,
            MaxBytesPerUnit = opts.MaxBytesPerUnit,
            MaxProbeUnitsPerUnit = opts.MaxProbeUnitsPerUnit,
            RequirePositiveBytes = opts.RequirePositiveBytes,
            BytesWithinBudget = bytesWithin,
            TimeWithinBudget = timeWithin,
            TimeAsserted = timeBudgeted && trusted,
            TimeStandDownReason = standDown,
        };

        // MEASURE → REPORT → ASSERT. The report is printed here, before any
        // assertion runs, so a red gate still leaves the numbers that produced
        // it in the log and in the job summary.
        Console.WriteLine(result.Report);

        return result;
    }

    /// <summary>
    ///     Synchronous overload: a window that provably cannot await.
    /// </summary>
    public static Task<PerfGateResult> MeasureAsync(
        Func<int, PerfGateLeg> work,
        int units,
        PerfGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return MeasureAsync(
            (Func<int, Task<PerfGateLeg>>)(u => Task.FromResult(work(u))),
            units,
            options);
    }

    /// <summary>
    ///     The yardstick. A fixed, deterministic, data-dependent integer
    ///     workload: its cost IS the runner's speed, and the same cost is
    ///     produced by every gate, so every gate's budget is written in the
    ///     same currency.
    /// </summary>
    private static double MeasureProbeMsPerUnit()
    {
        // Warm the probe itself: the first sample pays its tiered-JIT
        // compilation, which would read as a slow runner.
        _ = RunProbeSample();

        double[] samples = new double[ProbeSamples];
        for (int i = 0; i < ProbeSamples; i++)
        {
            samples[i] = RunProbeSample();
        }

        return Median(samples) / ProbeSampleMixes;
    }

    /// <summary>One probe sample: the wall time of <see cref="ProbeSampleMixes" /> mixes.</summary>
    private static double RunProbeSample()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long accumulator = 0;
        for (int i = 0; i < ProbeSampleMixes; i++)
        {
            accumulator += Mix();
        }

        sw.Stop();

        // The accumulator is consumed so the JIT cannot elide the loop.
        GC.KeepAlive(accumulator);
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>One probe unit: a fixed, data-dependent integer mix.</summary>
    private static long Mix()
    {
        ulong acc = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < ProbeMixSteps; i++)
        {
            acc = (acc ^ (acc >> 29)) * 0xBF58476D1CE4E5B9UL;
        }

        return (long)acc;
    }

    /// <summary>
    ///     GC.Collect(2) + WaitForPendingFinalizers + GC.Collect(2): a
    ///     collection that lands inside one leg and not the other was half the
    ///     ratio spikes the older gates produced.
    /// </summary>
    private static void Fence()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    /// <summary>The middle value of an odd or even sample (upper median).</summary>
    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static long Median(long[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    /// <summary>Max / min over the sample; 1.0 for a single sample.</summary>
    private static double Spread(double[] values)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (double value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        return min <= 0.0 ? double.PositiveInfinity : max / min;
    }
}
