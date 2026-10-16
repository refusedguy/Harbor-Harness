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
//
// The alloc leg is exact-zero rather than a ratio, deliberately: 2 wasted
// strings per rule is still LINEAR, so alloc(2N)/alloc(N) ~= 2.0 passes with
// the bug in place — a ratio gate cannot see this regression. Zero is
// portable where milliseconds are not (precedent:
// Fit_AllocatesNothing_WhenTheRowIsResolvedByDroppingSegments), so the N-rule
// and 2N-rule windows must both allocate 0 B.
[NotInParallel]
public sealed class MatchesPatternScalingGates
{
    private const int Rules = 256;
    private const double GrowthLimit = 3.0;
    private const int Rounds = 5;
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

    private static (double Ms, long Allocated) DriveEvaluates(PermissionRuleset ruleset)
    {
        // Stopwatch first: its own allocation must fall outside the window the
        // exact-zero alloc leg reads.
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < CallsPerWindow; i++)
        {
            var action = ruleset.Evaluate("read", ArgPath);
            if (action != PermissionAction.Ask)
            {
                throw new InvalidOperationException("Gate measured a match instead of a full miss-walk.");
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

        // Warm-up (tiered JIT), discarded.
        _ = DriveEvaluates(small);

        long allocSmall = long.MaxValue;
        long allocLarge = long.MaxValue;
        double msSmall = double.MaxValue;
        double msLarge = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            var s = DriveEvaluates(small);
            var l = DriveEvaluates(large);
            allocSmall = Math.Min(allocSmall, s.Allocated);
            allocLarge = Math.Min(allocLarge, l.Allocated);
            msSmall = Math.Min(msSmall, s.Ms);
            msLarge = Math.Min(msLarge, l.Ms);
        }

        double timeRatio = msLarge / msSmall;

        Console.WriteLine(
            $"matches-pattern-scaling: {Rules} rules = {msSmall:F3} ms / {allocSmall} B, "
            + $"{Rules * 2} rules = {msLarge:F3} ms / {allocLarge} B "
            + $"(time x{timeRatio:F2}, limit x{GrowthLimit:F2}, best of {Rounds}; "
            + "the absolute columns are informational and runner-dependent)");

        await Assert.That(msSmall)
            .IsGreaterThan(0.0)
            .Because("the time baseline must be a real measurement: msSmall = " + msSmall.ToString("F3") + " ms.");
        await Assert.That(timeRatio)
            .IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                "time(2N)/time(N) = " + timeRatio.ToString("F2") + " against a limit of "
                + GrowthLimit.ToString("F2") + ". Linear is 2.00 and O(N^2) is 4.00.");

        await Assert.That(allocSmall)
            .IsEqualTo(0L)
            .Because(
                "a miss-walk over " + Rules + " rules must allocate nothing: the two "
                + "per-call canonical strings are gone (#1528), but measured "
                + allocSmall + " B.");
        await Assert.That(allocLarge)
            .IsEqualTo(0L)
            .Because(
                "a miss-walk over " + (Rules * 2) + " rules must allocate nothing either, "
                + "but measured " + allocLarge + " B.");
    }
}
