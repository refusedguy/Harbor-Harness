using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.TestKit;

// TestScalingGate.cs — #1652: the SHARED measure-and-compare skeleton behind
// every relative cost gate (ratio-perf gates used to re-write it by hand).
//
// Three flaky gates in one CI day (#1642/#1643/#1649, all green on rerun,
// none touching the measured code) shared one anatomy: a leg of 1–25 ms where
// scheduler/GC/ neighbour noise doubles the ratio, best-of-N statistics that
// pick luck rather than a robust average, no fence between legs, no noise
// band, no escape hatch for a runner that simply cannot measure the leg.
// This type is the fix, and the contract every cost gate should ride:
//
//   1. LEG SCALING — a warm-up probe measures the small leg at
//      `initialUnits` units of work and the unit count is raised until one
//      leg needs >= `TargetLegMs` (50–100 ms) of wall time, and the stop a
//      "big enough" probe suggests must be CONFIRMED by a second probe: a
//      polluted probe must not freeze the legs short (the #1652 ui run
//      decided its ratio on 15 ms legs after exactly such a verdict). The
//      flaked gates ran 1–25 ms legs; at 10x that length, any transient
//      that lands in one leg inflates the ratio by at most ~10%, not by
//      2–4x.
//   2. MEDIAN over ROUNDS — the leg pair is measured `Rounds` (>= 5) times
//      and the MEDIANS form both ratios. Best-of-N tracked the luckiest round
//      (a descheduled or collected leg only inflates a leg, so best-of *can*
//      bound the damage, but it silently isolates one lucky pair instead of
//      reporting what the machine actually does).
//   3. GC FENCE between legs — GC.Collect(2) + WaitForPendingFinalizers +
//      GC.Collect(2) before every measured leg, so a collection can no longer
//      land in the middle of one leg and not the other.
//   4. EPSILON on the TIME branch only — the time ratio is compared against
//      `GrowthLimit × 1.05`: wall-clock legs (even 100 ms ones) carry runner
//      noise, so the bound gets an explicit 5% band instead of an implicit
//      "hopefully nobody collects mid-leg". The ALLOCATION branch stays
//      STRICT at `GrowthLimit`: allocation is the runner-independent half of
//      every claim, and GateResult offers no band on it.
//   5. SKIP-IF-UNMEASURABLE — if, after scaling, the measured small leg is
//      still under `MinMeasurableLegMs` (5 ms), the gate reports it and the
//      test SKIPS instead of inviting the runner to decide the ratio from
//      noise. An honest Skip is cheaper than a false red in a merge chain.
//
// WHAT A "UNIT" IS — the caller decides. A unit is one repetition of the
// work whose scaling is being gated: one StoreCost drive, one burst of N
// argv splits, one BuildRequest with N tool calls. The helper never
// amplifies N itself; it repeats the same unit many times so that a leg
// lasts long enough for its DURATION and ALLOCATION to be measured against
// real work instead of against the measurement's own overhead.
//
// WHO MAY REFERENCE THIS — ANY cost-gate project may reference
// Harbor.TestKit since #1889. The copies of this file used to exist because
// the shared TestKit dragged Harbor.Application, and a test project must not
// pull that into a renderer/provider/domain slice it is trying to measure.
// #1889 split the kit into a core and a UI part, and the CORE's closure is
// now Harbor.Abstractions + Harbor.Abstractions.Contracts + itself — the
// heavy assembly is gone — so the five DOCUMENTED SAME-API INTERNAL COPIES
// of this file (Harbor.Abstractions.Tests, Harbor.Domain.Tests,
// Harbor.Providers.Tests, Harbor.Tui.PerfTests, Harbor.Ui.Framework.Tests)
// were deleted and those projects now consume this file directly.
//
// A copy is still allowed, but only with a WRITTEN REASON: a copy must name
// the file it was copied from and say why it is a copy. That is enforced by
// tests/Harbor.Architecture.Tests/TestKitSharedCopyReasonRules.cs.

/// <summary>
///     One measured leg of a scaling gate: the wall time of the window and
///     the bytes allocated inside it.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Allocated" /> is whatever probe the caller chose
///         (per-thread for a synchronous window, process-wide via
///         <see cref="AllocationProbe.MeasureProcessAsync(Func{Task})" /> when
///         the body spans an <c>await</c>); gates with an exact-zero
///         allocation contract keep reading their probe in the leg body. When
///         the gate is time-only, pass <c>0</c> and set
///         <see cref="TestScalingGateOptions.MeasureAllocations" /> to
///         <c>false</c> so the report and the ratio asserts leave allocation
///         alone.
///     </para>
/// </remarks>
public readonly record struct TestScalingGateLeg(double Ms, long Allocated);

/// <summary>Tuning knobs for <see cref="TestScalingGate.MeasureAsync" />.</summary>
public sealed record TestScalingGateOptions
{
    /// <summary>Log-facing name of the gate, printed on the deciding line.</summary>
    public string Label { get; init; } = "scaling-gate";

    /// <summary>Growth allowed for a doubled input, before any noise band.</summary>
    public double GrowthLimit { get; init; } = 3.0;

    /// <summary>
    ///     Measured leg pairs. Five: every leg after the first four rounds and
    ///     warm-ups is a repeat of the same work, so pairs are cheap, and a
    ///     median needs headroom over noise — the flaked gates ran exactly
    ///     five best-of rounds, which is enough once the statistic is a
    ///     median.
    /// </summary>
    public int Rounds { get; init; } = 5;

    /// <summary>
    ///     Wall time one leg must reach before measuring (millisecond legs are
    ///     the source of all three #1652 flakes; 50–100 ms shrinks a
    ///     scheduler/GC transient's ratio impact to a few percent).
    /// </summary>
    public double TargetLegMs { get; init; } = 100.0;

    /// <summary>
    ///     Below this median leg duration the gate SKIPS instead of asserting:
    ///     a leg shorter than ~5 ms decides nothing on a hosted runner.
    /// </summary>
    public double MinMeasurableLegMs { get; init; } = 5.0;

    /// <summary>
    ///     Hard ceiling on the scaled unit count, guarding against a body
    ///     whose per-unit duration estimates into an unbounded amplification.
    /// </summary>
    public int MaxUnits { get; init; } = 100_000;

    /// <summary>
    ///     Whether the legs carry an allocation measurement. <c>false</c> for
    ///     time-only gates; the helper then omits the alloc columns from the
    ///     report and never asserts an alloc ratio (a ratio over "0 / 0 B"
    ///     would be a number invented for the caller, not a measurement).
    /// </summary>
    public bool MeasureAllocations { get; init; } = true;
}

/// <summary>
///     Outcome of <see cref="TestScalingGate.MeasureAsync" />: the measured
///     medians, the derived ratios, and the assert/skip surface the gate
///     calls.
/// </summary>
public sealed record TestScalingGateResult
{
    public required string Label { get; init; }
    public required double GrowthLimit { get; init; }
    public required int Rounds { get; init; }
    public required bool MeasureAllocations { get; init; }

    /// <summary>Units of work per measured leg, after leg scaling.</summary>
    public required int UnitsPerLeg { get; init; }

    /// <summary>Median wall time of the small (N) leg across all rounds, in ms.</summary>
    public required double MsSmall { get; init; }

    /// <summary>Median wall time of the large (2N) leg across all rounds, in ms.</summary>
    public required double MsLarge { get; init; }

    /// <summary>Median allocation of the small leg, when <see cref="MeasureAllocations" />.</summary>
    public long AllocSmall { get; init; }

    /// <summary>Median allocation of the large leg, when <see cref="MeasureAllocations" />.</summary>
    public long AllocLarge { get; init; }

    /// <summary>
    ///     The measured small leg sits under <c>MinMeasurableLegMs</c> even
    ///     after scaling: the gate must SKIP rather than let runner noise pick
    ///     a ratio.
    /// </summary>
    public bool Unmeasurable { get; init; }

    /// <summary>Reason for the skip, when <see cref="Unmeasurable" />.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Time ratio large/small, over the leg medians.</summary>
    public double TimeRatio => MsLarge / MsSmall;

    /// <summary>Allocation ratio large/small, over the leg medians.</summary>
    public double AllocRatio => (double)AllocLarge / AllocSmall;

    /// <summary>
    ///     The widening applied to the time bound before comparing
    ///     (<c>1.05</c>, #1652). Allocation stays strict.
    /// </summary>
    public double EffectiveTimeLimit => GrowthLimit * TestScalingGate.TimeEpsilon;

    /// <summary>Decision on the time branch, with the noise band applied.</summary>
    public bool TimeWithinLimit =>
        TestScalingGate.WithinLimit(MsSmall, MsLarge, GrowthLimit * TestScalingGate.TimeEpsilon);

    /// <summary>Decision on the allocation branch — STRICT, no band.</summary>
    public bool AllocWithinLimit =>
        TestScalingGate.WithinLimit(AllocSmall, AllocLarge, GrowthLimit);

    /// <summary>
    ///     The deciding measurements and ratios, printed even when the gate
    ///     passes so the reader sees the numbers without opening a failure
    ///     message. Absolute columns stay informational: only the ratios are
    ///     gated.
    /// </summary>
    public string Report
    {
        get
        {
            string allocPart = MeasureAllocations
                ? $" / {AllocSmall} B, large {MsLarge:F3} ms / {AllocLarge} B"
                : string.Empty;
            string ratioPart = $"(time x{TimeRatio:F2}, time-limit x{EffectiveTimeLimit:F2} = {GrowthLimit:F2} x{TestScalingGate.TimeEpsilon:F2} band #1652";

            if (MeasureAllocations)
            {
                ratioPart += $", alloc x{AllocRatio:F2} (strict limit x{GrowthLimit:F2})";
            }

            ratioPart += $", median of {Rounds} legs of {UnitsPerLeg} units)";

            return $"[scaling] {Label}: small {MsSmall:F3} ms{allocPart} — {ratioPart}";
        }
    }

    /// <summary>
    ///     Asserts the gate. In order: an unmeasurable leg SKIPS; the small
    ///     legs are shown to be real measurements; the time ratio is checked
    ///     against <see cref="EffectiveTimeLimit" />; optionally the
    ///     allocation ratio is checked against the strict
    ///     <see cref="GrowthLimit" />.
    /// </summary>
    /// <param name="topic">
    ///     What the ratio is named in the test's own terms, e.g.
    ///     "dispatch(2N)/dispatch(N)".
    /// </param>
    /// <param name="linearWhy">
    ///     Why the bound separates linear from quadratic for THIS gate, if the
    ///     default sentence does not fit.
    /// </param>
    /// <param name="assertAllocRatio">
    ///     <c>false</c> for time-only gates and for gates whose allocation
    ///     contract is a strict EXACT ZERO (they assert it in the test body on
    ///     <see cref="AllocSmall" /> / <see cref="AllocLarge" />); a ratio
    ///     check would divide by zero and error out for the wrong reason.
    /// </param>
    public async Task AssertWithinLimitsAsync(
        string topic,
        string? linearWhy = null,
        bool assertAllocRatio = true)
    {
        if (Unmeasurable)
        {
            Console.WriteLine($"[scaling] {Label}: SKIP — {SkipReason}");
            Skip.Test(SkipReason!);
            return;
        }

        Console.WriteLine(Report);

        await Assert.That(MsSmall).IsGreaterThan(0.0)
            .Because(
                $"the small-leg baseline must be a real measurement ({topic}, {Label}): "
                + $"{MsSmall:F3} ms over {UnitsPerLeg} units; a zero there means nothing was measured "
                + "(the #591 shape: a rule that cannot tell 'no cost' from 'no measurement').");

        await Assert.That(TimeRatio).IsLessThanOrEqualTo(EffectiveTimeLimit)
            .Because(
                $"{topic} = {TimeRatio:F2} against a limit of {EffectiveTimeLimit:F2} "
                + $"(declared {GrowthLimit:F2}, widened by the x{TestScalingGate.TimeEpsilon:F2} noise band, #1652): "
                + $"{MsLarge:F3} ms versus {MsSmall:F3} ms, median of {Rounds} measured legs of {UnitsPerLeg} units. "
                + (linearWhy ?? "Linear is 2.00 and O(N^2) is 4.00."));

        if (assertAllocRatio && MeasureAllocations)
        {
            await Assert.That(AllocSmall).IsGreaterThan(0L)
                .Because(
                    $"the small-leg allocation baseline must be a real measurement ({topic}, {Label}): "
                    + $"{AllocSmall} B; a zero denominator makes the alloc gate meaningless (#591).");

            await Assert.That(AllocRatio).IsLessThanOrEqualTo(GrowthLimit)
                .Because(
                    $"{topic} on allocations = {AllocRatio:F2} against the STRICT limit {GrowthLimit:F2} "
                    + "(allocation carries no noise band, #1652): "
                    + $"{AllocLarge} B versus {AllocSmall} B, median of {Rounds} measured legs. "
                    + "Allocations do not move with the runner's speed at all, so this ratio holds on every machine.");
        }
    }
}

/// <summary>
///     The shared measure-and-compare skeleton behind every relative cost
///     gate (#1652). See the file header for what it hardens and why.
/// </summary>
public static class TestScalingGate
{
    /// <summary>
    ///     The noise multiple applied to the TIME bound only — wall-clock legs
    ///     carry runner noise that the counts do not; the allocation branch is
    ///     compared against the bare limit.
    /// </summary>
    public const double TimeEpsilon = 1.05;

    /// <summary>
    ///     Probe budget for leg scaling. Each attempt grows the unit count by
    ///     at most x8 toward <see cref="TestScalingGateOptions.TargetLegMs" />,
    ///     and a "big enough" probe verdict must be confirmed by a second
    ///     probe before it stops the scaling — eight attempts give a small
    ///     <c>initialUnits</c> enough cap-depth to actually reach the target
    ///     while MaxUnits remains the hard ceiling.
    /// </summary>
    private const int MaxScaleAttempts = 8;

    /// <summary>
    ///     The double in a single expression, so every gate's positive-control
    ///     table pins the SAME decision function: a zero or negative baseline
    ///     is refused (a ratio over a baseline that was never measured would
    ///     be the #591 shape) and the bound itself accepts an exact hit.
    /// </summary>
    public static bool WithinLimit(double small, double large, double limit) =>
        small > 0.0 && large <= small * limit;

    /// <summary>
    ///     Measures both legs of a scaling gate with the #1652 recipe: fences,
    ///     warm-ups, leg scaling to a measurable duration, <see cref="TestScalingGateOptions.Rounds" />
    ///     rounds, median statistics.
    /// </summary>
    /// <param name="measureSmallLeg">
    ///     Runs <c>units</c> units of the SMALL (N) work in one measured
    ///     window. A GC fence is taken immediately before every call, and must
    ///     there run the stopwatch and allocate-counter bookkeeping — and then
    ///     loop <c>units</c> times over the unit code.
    /// </param>
    /// <param name="measureLargeLeg">
    ///     Same for the LARGE (2N) work, at the same <c>units</c> count so the
    ///     loop skeleton cancels in the ratio.
    /// </param>
    /// <param name="initialUnits">
    ///     The unit count the gate previously used as its measured window —
    ///     the scaling probe starts here and only raises it.
    /// </param>
    /// <param name="options">Tuning; defaults carry the #1652 recipe.</param>
    public static async Task<TestScalingGateResult> MeasureAsync(
        Func<int, Task<TestScalingGateLeg>> measureSmallLeg,
        Func<int, Task<TestScalingGateLeg>> measureLargeLeg,
        int initialUnits,
        TestScalingGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(measureSmallLeg);
        ArgumentNullException.ThrowIfNull(measureLargeLeg);
        TestScalingGateOptions opts = options ?? new TestScalingGateOptions();
        if (opts.Rounds < 3)
        {
            throw new InvalidOperationException(
                $"[TestScalingGate] {opts.Label}: Rounds = {opts.Rounds} is below the median's minimum of 3.");
        }

        int units = Math.Max(1, initialUnits);

        // A mid-leg transient only ever inflates a leg, so scaling the unit
        // count by the measured small-leg duration is monotone and safe. But
        // the same noise cuts both ways: a PROBE caught in a CI-fleet hiccup
        // can measure far above the steady rate, and the stop that "leg is
        // big enough" then suggests would freeze the legs SHORT — the #1652
        // ui run scaled only to 262 units on exactly such a probe and went on
        // to decide a 3.44-over-3.15 ratio on 15 ms legs. So the "reached
        // TargetLegMs" verdict is CONFIRMED by a second probe at the same
        // units before it is trusted, and scaling continues — from the
        // confirming measurement, which is the warm per-unit estimate — until
        // two probes agree or a bound (attempts, MaxUnits) ends the loop.
        // Every probe still doubles as a tiered-JIT warm-up of the measured
        // path, and each attempt scales by at least one unit, so growth stays
        // monotone and MaxUnits remains the hard ceiling.
        double probeMs = 0.0;
        for (int attempt = 0; attempt < MaxScaleAttempts; attempt++)
        {
            Fence();
            TestScalingGateLeg probe = await measureSmallLeg(units).ConfigureAwait(false);
            probeMs = probe.Ms;
            if (probeMs <= 0.0 || units >= opts.MaxUnits)
            {
                break;
            }

            if (probeMs >= opts.TargetLegMs)
            {
                Fence();
                TestScalingGateLeg confirm = await measureSmallLeg(units).ConfigureAwait(false);
                if (confirm.Ms >= opts.TargetLegMs)
                {
                    break;
                }

                probeMs = confirm.Ms; // the confirming measurement is the warm estimate
            }

            long scaled = (long)Math.Ceiling(units * Math.Min(8.0, opts.TargetLegMs / probeMs));
            units = (int)Math.Min(opts.MaxUnits, Math.Max(units + 1, scaled));
        }

        // Warm the LARGE leg with the final unit count so its first measured
        // round does not pay tiered-JIT compilation either.
        Fence();
        _ = await measureLargeLeg(units).ConfigureAwait(false);

        double[] msSmall = new double[opts.Rounds];
        double[] msLarge = new double[opts.Rounds];
        long[] allocSmall = new long[opts.Rounds];
        long[] allocLarge = new long[opts.Rounds];
        for (int round = 0; round < opts.Rounds; round++)
        {
            Fence();
            TestScalingGateLeg smallLeg = await measureSmallLeg(units).ConfigureAwait(false);
            Fence();
            TestScalingGateLeg largeLeg = await measureLargeLeg(units).ConfigureAwait(false);

            msSmall[round] = smallLeg.Ms;
            msLarge[round] = largeLeg.Ms;
            if (opts.MeasureAllocations)
            {
                allocSmall[round] = smallLeg.Allocated;
                allocLarge[round] = largeLeg.Allocated;
            }
        }

        double medianSmall = Median(msSmall);
        double medianLarge = Median(msLarge);

        bool unmeasurable = medianSmall <= 0.0 || medianSmall < opts.MinMeasurableLegMs;
        string? skipReason = unmeasurable
            ? $"leg too small to measure on hosted runner: median {medianSmall:F3} ms "
            + $"over {units} units < {opts.MinMeasurableLegMs:F0} ms after #1652 leg scaling ({opts.Label}); "
            + "an honest Skip instead of a ratio picked by noise."
            : null;

        return new TestScalingGateResult
        {
            Label = opts.Label,
            GrowthLimit = opts.GrowthLimit,
            Rounds = opts.Rounds,
            MeasureAllocations = opts.MeasureAllocations,
            UnitsPerLeg = units,
            MsSmall = medianSmall,
            MsLarge = medianLarge,
            AllocSmall = Median(allocSmall),
            AllocLarge = Median(allocLarge),
            Unmeasurable = unmeasurable,
            SkipReason = skipReason,
        };
    }

    /// <summary>
    ///     GC.Collect(2) + WaitForPendingFinalizers + GC.Collect(2): a
    ///     collection that lands INSIDE one leg and not the other was half the
    ///     ratio spikes, so every measured leg starts from a collect.
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
}
