using System.Diagnostics;
using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Tests;

// MatchesPatternScalingGates — #1528 (hot-path slice of #97, per-tool-call):
// PermissionRule.MatchesPattern used to allocate 2 strings per call
// (Pattern.Replace + argPath.Replace before the cached-regex match), once per
// rule evaluated. The canonicalisation now lives in the compile path
// (BoundedPatternRegexCache.CompileGlob, cache miss only) with a separator
// class matching both '/' and '\', so a miss-walk allocates nothing.
//
// GATE: relative, same shape as #410 / #1373. Cost(2N)/cost(N) <= 3 for time
// over N-rule Evaluate miss-walks (linear is 2.00, O(N^2) is 4.00). No
// absolute millisecond budget: those measure the runner, not the code.
// Measured through the DOCUMENTED MINI-COPY of Harbor.TestKit's
// TestScalingGate (#1652; Abstractions.Tests must not reference TestKit — it
// drags Harbor.Application): GC-fenced legs scaled toward ~100 ms of real
// work, medians over five rounds, x1.05 noise band on the time branch;
// #1642's ratio-3.63-out-of-a-20-ms-best-of leg is the shape that dies here.
//
// The alloc leg is exact-zero rather than a ratio, deliberately: 2 wasted
// strings per rule is still LINEAR, so alloc(2N)/alloc(N) ~= 2.0 passes with
// the bug in place — a ratio gate cannot see this regression. Zero is
// portable where milliseconds are not (precedent:
// Fit_AllocatesNothing_WhenTheRowIsResolvedByDroppingSegments), so the N-rule
// and 2N-rule windows must both allocate 0 B; the helper measures the
// allocation but is told NOT to ratio-assert it (a ratio over "0 / 0 B" is a
// number invented for the caller), so the exact-zero asserts below stand.
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
    private const int Rules = 256;
    private const double GrowthLimit = 3.0;
    private const int CallsPerWindow = 200;
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
    ///     One measured leg: <paramref name="units" /> burst windows worth of
    ///     full miss-walks over <paramref name="ruleset" />, timed and
    ///     allocation-counter'd as one window. The zero-alloc claim scales
    ///     with the unit count — a miss-walk allocates nothing, so the
    ///     scaled window must still land at exactly 0 B.
    /// </summary>
    private static (double Ms, long Allocated) DriveEvaluates(PermissionRuleset ruleset, int units)
    {
        // Stopwatch first: its own allocation must fall outside the window the
        // exact-zero alloc leg reads.
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int u = 0; u < units; u++)
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
        return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Cost_GrowsLinearly_InRuleCount()
    {
        var small = BuildRuleset(Rules);
        var large = BuildRuleset(Rules * 2);

        // Non-vacuity (#901 shape): the subjects are real — N and 2N rules —
        // and every call walks all of them to the Ask default.
        await Assert.That(small.Rules.Count).IsEqualTo(Rules);
        await Assert.That(large.Rules.Count).IsEqualTo(Rules * 2);
        await Assert.That(small.Evaluate("read", ArgPath)).IsEqualTo(PermissionAction.Ask);
        await Assert.That(large.Evaluate("read", ArgPath)).IsEqualTo(PermissionAction.Ask);

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(AsLeg(DriveEvaluates(small, units))),
            measureLargeLeg: units => Task.FromResult(AsLeg(DriveEvaluates(large, units))),
            initialUnits: CallsPerWindow,
            options: new TestScalingGateOptions
            {
                Label = "matches-pattern-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "time(2N)/time(N)",
            linearWhy: "Linear is 2.00 and O(N^2) is 4.00.",
            assertAllocRatio: false); // exact-zero contract below, not a ratio.

        await Assert.That(result.AllocSmall)
            .IsEqualTo(0L)
            .Because(
                "a miss-walk over " + Rules + " rules must allocate nothing: the two "
                + "per-call canonical strings are gone (#1528), but measured "
                + result.AllocSmall + " B.");
        await Assert.That(result.AllocLarge)
            .IsEqualTo(0L)
            .Because(
                "a miss-walk over " + (Rules * 2) + " rules must allocate nothing either, "
                + "but measured " + result.AllocLarge + " B.");
    }

    private static TestScalingGateLeg AsLeg((double Ms, long Allocated) leg) => new(leg.Ms, leg.Allocated);
}
