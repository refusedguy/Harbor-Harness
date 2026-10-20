using System.Diagnostics;
using Harbor.Abstractions.Permissions;
using Harbor.TestKit;

namespace Harbor.Abstractions.Tests;

// MatchesPatternScalingGates — #1528 (hot-path slice of #97, per-tool-call):
// PermissionRule.MatchesPattern used to allocate 2 strings per call
// (Pattern.Replace + argPath.Replace before the cached-regex match), once per
// rule evaluated. The canonicalisation now lives in the compile path
// (BoundedPatternRegexCache.CompileGlob, cache miss only) with a separator
// class matching both '/' and '\', so a miss-walk allocates nothing.
//
// GATE: a BUDGET PER UNIT OF WORK (#1948), through the SHARED
// Harbor.TestKit's PerfGate (#1747): 0 B per Evaluate call (machine-
// independent, HARD) and 3 000 probe units per call (machine-scaled time,
// SOFT), over a FIXED amount of work - 2 400 calls against the 512-rule
// ruleset. Since #1889 this project references the shared file instead of
// keeping a local copy: the TestKit core no longer drags Harbor.Application
// into the slice this project measures.
//
// THE LEG LENGTH, AND WHY IT WAS 8-17x OVER TARGET (#1948). The gate used to
// be RELATIVE - Cost(2N)/cost(N) <= 3 for time over N-rule Evaluate
// miss-walks - measured through the shared TestScalingGate (#1652), whose leg
// SCALER only ever RAISES the unit count: it starts at `initialUnits` and
// grows until a leg lasts ~100 ms. This gate started it at
// `initialUnits = CallsPerWindow = 200`, and one unit is already a burst of
// 200 Evaluate calls, so the probe read 876-1726 ms per leg: 8-17x the
// target, and the gate paid that on every probe, every confirmation, the
// large-leg warm-up and all five measured rounds. The measured receipt is run
// 39423151864: the small leg 876.361 ms and the large one 1 838.109 ms, and
// the test itself took 17.4 s - 96 % of the wall time of
// `Harbor.Abstractions.Tests` (29.0 s) and 19 % of the non-architecture time
// of the `core` shard. The ratio never moved while that was happening
// (x2.03-x2.21 across seven runs), so 26.5 s were spent proving a number
// that did not change.
//
// THE ALLOCATION CLAIM IS THE EXACT ZERO, AND IT IS THE POINT. 2 wasted
// strings per rule is still LINEAR, so alloc(2N)/alloc(N) ~= 2.0 passes with
// the bug in place - a ratio gate cannot see that regression. Zero is
// portable where milliseconds are not (precedent:
// Fit_AllocatesNothing_WhenTheRowIsResolvedByDroppingSegments), so the
// miss-walk window must allocate 0 B, and the budget says exactly that.
//
// [NotInParallel] is the BARE form, and it is load-bearing (#1790). The gate
// reads wall-clock elapsed, and elapsed time is not thread-local: a
// neighbouring test burning cores — or, under the server GC every CI job
// enables, a collection that pauses this thread mid-window — decides the
// ratio. The receipt is run 38129838108 (PR #1285, commit b5db32e2), where
// the very same tree measured time x3.10 against the x3.00 limit while the
// same run measured alloc x1.99: textbook linear memory with an inflated
// stopwatch, on a tree that had passed the identical 536-test binary forty
// minutes earlier. A NAMED key would not have helped — it excludes only
// same-key peers, and every other class in this assembly still runs
// alongside — so the only form that delivers "nothing else runs in this
// window" is the bare one. See docs/TEST_PATTERNS.md §Parallelism.
[NotInParallel]
public sealed class MatchesPatternScalingGates
{
    /// <summary>Rules in the SMALL ruleset the gate measures against.</summary>
    private const int Rules = 256;

    /// <summary>
    ///     Rules in the ruleset the gate MEASURES at - twice
    ///     <see cref="Rules" />, because a per-call cost that grows with the
    ///     rule count must land in the measurement instead of hiding behind
    ///     the smaller ruleset (the same argument
    ///     <c>ProjectHistoryScalingGates</c> makes).
    /// </summary>
    private const int MeasuredRules = Rules * 2;

    /// <summary>Evaluate calls in one burst window.</summary>
    private const int CallsPerWindow = 200;

    /// <summary>
    ///     Evaluate calls per measured leg - the FIXED amount of work. 2 400
    ///     calls is 12 burst windows over the 512-rule ruleset, about 100 ms of
    ///     real work on the CI runner.
    /// </summary>
    private const int CallsPerLeg = 2_400;

    /// <summary>
    ///     Allocation budget for ONE Evaluate call, in bytes. The miss-walk
    ///     allocates NOTHING - that is the whole point of the #1528 rewrite -
    ///     so the budget is the EXACT ZERO, the strongest shape a byte claim
    ///     can take. The two canonical strings the old path built per rule
    ///     (#1528) would land here as kilobytes per call.
    /// </summary>
    private const long MaxBytesPerCall = 0;

    /// <summary>
    ///     Time budget for ONE Evaluate call, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 876.361 ms over 40 000 calls = 21.9 us per call
    ///     against a probe that costs 0.000380 ms/unit, i.e. ~55 probe units
    ///     per call at 256 rules and ~110 at 512.
    ///     <para>
    ///         The budget is wide on purpose: the probe's OWN measured cost
    ///         moved 5.3x between two CI runs of the SAME tree (0.000380
    ///         ms/unit in run 39423151864, 0.000072 ms/unit in run
    ///         39436405496), so the probe-unit currency is only as stable as
    ///         the yardstick behind it. The BYTE budget - the exact zero - is
    ///         the branch that can fail on any runner.
    ///     </para>
    /// </summary>
    private const double MaxProbeUnitsPerCall = 3_000.0;

    private const string ArgPath = "src/evaluate/target.cs";

    private static PermissionRuleset BuildRuleset(int count)
    {
        var rules = new PermissionRule[count];
        for (int i = 0; i < count; i++)
        {
            rules[i] = new PermissionRule("read", $"zz-nomatch-{i:000000}/*", PermissionAction.Ask);
        }

        // Empty policies: the gate isolates the MatchesPattern slice, not the
        // safety-policy work around it.
        return new PermissionRuleset(rules, Array.Empty<IArgSafetyPolicy>());
    }

    /// <summary>
    ///     One measured leg: <paramref name="calls" /> full miss-walks over
    ///     <paramref name="ruleset" />, timed and allocation-counter'd as one
    ///     window. The zero-alloc claim scales with the call count - a
    ///     miss-walk allocates nothing, so the measured window must still land
    ///     at exactly 0 B.
    /// </summary>
    private static PerfGateLeg DriveEvaluates(PermissionRuleset ruleset, int calls)
    {
        // Stopwatch first: its own allocation must fall outside the window the
        // exact-zero alloc leg reads.
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int bursts = calls / CallsPerWindow;
        for (int u = 0; u < bursts; u++)
        {
            for (int i = 0; i < CallsPerWindow; i++)
            {
                var action = ruleset.Evaluate("read", ArgPath);
                if (action != PermissionAction.Ask)
                {
                    throw new InvalidOperationException("Gate measured a match instead of a full miss-walk.");
                }
            }
        }

        sw.Stop();
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    ///     THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
    ///     to be <c>t(2N) &lt;= 3*t(N)</c> over 256 against 512 rules, decided
    ///     by the shared TestScalingGate (#1652), and it paid
    ///     <strong>876-1 838 ms per leg</strong> against a 100 ms target:
    ///     the leg scaler only RAISES the unit count, and this gate started it
    ///     at a burst of 200 <c>Evaluate</c> calls, so every probe, every
    ///     confirmation, the warm-up and all five measured rounds carried a
    ///     leg 8-17x over the target. The measured receipt is run 39423151864:
    ///     small leg 876.361 ms / 0 B, large leg 1 838.109 ms / 0 B, and the
    ///     test itself 17.4 s - 96 % of the wall time of
    ///     <c>Harbor.Abstractions.Tests</c> (29.0 s). The ratio never moved
    ///     while that was happening (x2.03-x2.21 across seven runs), so 26.5 s
    ///     of the gate's 27.9 s went to proving a number that did not change.
    ///     <para>
    ///         The gate is now:
    ///         <code>
    ///         bytes per Evaluate call   &lt;= 0      (machine-independent, HARD)
    ///         probe units per call      &lt;= 3 000  (machine-scaled time, SOFT)
    ///         </code>
    ///         measured over a FIXED amount of work (2 400 calls against the
    ///         512-rule ruleset - the LARGER one, so a per-call cost that
    ///         grows with the rule count lands in the measurement) on the SAME
    ///         runner in the SAME run. The allocation branch is the EXACT ZERO
    ///         a miss-walk must produce - the strongest shape a byte claim can
    ///         take, and the one that catches the #1528 regression the ratio
    ///         gate could not see (two canonical strings per rule are still
    ///         LINEAR, so a ratio passes with the bug in place). The time
    ///         branch is in PROBE UNITS and stands down when the rounds do not
    ///         reproduce each other. The report prints on every run.
    ///     </para>
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task Cost_PerCall_StaysWithinBudget()
    {
        var small = BuildRuleset(Rules);
        var large = BuildRuleset(MeasuredRules);

        // Non-vacuity (#901 shape): the subjects are real - N and 2N rules -
        // and every call walks all of them to the Ask default.
        await Assert.That(small.Rules.Count).IsEqualTo(Rules);
        await Assert.That(large.Rules.Count).IsEqualTo(MeasuredRules);
        await Assert.That(small.Evaluate("read", ArgPath)).IsEqualTo(PermissionAction.Ask);
        await Assert.That(large.Evaluate("read", ArgPath)).IsEqualTo(PermissionAction.Ask);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: calls => Task.FromResult(DriveEvaluates(large, calls)),
            units: CallsPerLeg,
            options: new PerfGateOptions
            {
                Label = "matches-pattern-cost",
                MaxBytesPerUnit = MaxBytesPerCall,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerCall,

                // A miss-walk legitimately allocates NOTHING, so zero is the
                // correct answer here and not a missing measurement.
                RequirePositiveBytes = false,
            });

        await result.AssertWithinBudgetsAsync("permission pattern miss-walk per Evaluate call");

        // The exact-zero contract, stated on its own terms: a miss-walk over
        // N rules must allocate nothing, and so must one over 2N rules.
        await Assert.That(result.BytesPerUnit)
            .IsEqualTo(0.0)
            .Because(
                "a miss-walk over " + Rules + " rules must allocate nothing: the two "
                + "per-call canonical strings are gone (#1528), but measured "
                + result.BytesPerUnit + " B per call.");
    }
}
