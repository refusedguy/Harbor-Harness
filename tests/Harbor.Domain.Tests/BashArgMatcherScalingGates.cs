using System.Diagnostics;
using Harbor.Abstractions.Permissions;

namespace Harbor.Domain.Tests;

// BashArgMatcherScalingGates.cs — #97: the bash argv path's SCALING is a merge
// gate, and the gate is RELATIVE to a measurement taken in the same run.
//
// WHAT: SplitArgv runs on every bash permission evaluation (per tool call),
// once per command segment plus once per segment inside the Allow-loop. The
// per-call budget used to include a compiler display class per split (the
// capturing local Flush()) on top of the List + StringBuilder + token
// strings the split fundamentally needs.
//
// GATE: cost(2N)/cost(N) <= 3 for both time and allocations, where N is the
// argv token count. Linear is 2.00, O(N^2) is 4.00 — same bound shape as the
// #410 store-path gate. No absolute millisecond or byte budget: those measure
// the runner, not the code. Measured through the DOCUMENTED MINI-COPY of
// Harbor.TestKit's TestScalingGate (#1652; this project must not reference
// TestKit — it drags Harbor.Application): GC-fenced legs scaled toward
// ~100 ms of real work, medians over five rounds, x1.05 noise band on the
// time branch only, the allocation branch strict.
//
// [NotInParallel] is the BARE form, and it is load-bearing (#1790). The time
// leg reads wall-clock elapsed, and elapsed time is not thread-local: a
// neighbour burning cores — or, under the server GC every CI job enables, a
// collection that pauses this thread mid-window — decides the ratio. Run
// 38129838108 (PR #1285) is the receipt: the same tree measured time x3.10
// against the x3.00 limit while the same run measured alloc x1.99 — linear
// memory with an inflated stopwatch, on a tree that had passed the identical
// binary forty minutes earlier. A NAMED key would not help: it excludes only
// same-key peers, and every other class in this assembly still runs
// alongside. See docs/TEST_PATTERNS.md §Parallelism.
[NotInParallel]
public sealed class BashArgMatcherScalingGates
{
    private const int Tokens = 256;
    private const double GrowthLimit = 3.0;
    private const int CallsPerWindow = 200;

    private static string BuildCommand(int tokens)
    {
        var sb = new System.Text.StringBuilder(tokens * 6 + 4);
        sb.Append("cmd");
        for (int i = 0; i < tokens; i++)
        {
            sb.Append(' ');
            sb.Append('a');
            sb.Append(i);
        }
        return sb.ToString();
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> burst windows worth of
    ///     splits over <paramref name="command" />, timed and allocation-counter'd
    ///     as one window. The #1652 leg scaler multiplies
    ///     <paramref name="units" /> until the window reaches a measurable
    ///     duration; the command (N vs 2N tokens) stays the dimension the gate
    ///     is about.
    /// </summary>
    private static TestScalingGateLeg DriveSplit(string command, int units)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int u = 0; u < units; u++)
        {
            for (int i = 0; i < CallsPerWindow; i++)
            {
                var argv = BashArgMatcher.SplitArgv(command);
                if (argv.Count == 0)
                {
                    throw new InvalidOperationException("SplitArgv measured nothing.");
                }
            }
        }

        sw.Stop();
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Cost_GrowsLinearly_InArgvTokens()
    {
        string small = BuildCommand(Tokens);
        string large = BuildCommand(Tokens * 2);

        // Non-vacuity: the doubled command really tokenizes to twice the argv.
        await Assert.That(BashArgMatcher.SplitArgv(small).Count).IsEqualTo(Tokens + 1);
        await Assert.That(BashArgMatcher.SplitArgv(large).Count).IsEqualTo(Tokens * 2 + 1);

        // Warm-up and leg scaling live in TestScalingGate (#1652): its probes
        // are discarded measurements, and the folded unit count raises the
        // window until one leg is too long for a scheduler transient to
        // double the ratio (the #1642 flake shape).
        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(DriveSplit(small, units)),
            measureLargeLeg: units => Task.FromResult(DriveSplit(large, units)),
            initialUnits: CallsPerWindow,
            options: new TestScalingGateOptions
            {
                Label = "bash-argv-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "time(2N)/time(N)",
            linearWhy: "Linear is 2.00 and O(N^2) is 4.00.");
    }
}
