using System.Diagnostics;
using Harbor.Abstractions.Permissions;
using Harbor.TestKit;

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
// the runner, not the code. Measured through the SHARED Harbor.TestKit's
// TestScalingGate (#1652; since #1889 this project references the shared file
// instead of keeping a local copy — the TestKit core no longer drags
// Harbor.Application into the slice this project measures): GC-fenced legs
// scaled toward
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
    /// <summary>Tokens in the small command the gate measures against.</summary>
    private const int Tokens = 256;

    /// <summary>
    ///     Tokens in the command the gate MEASURES at - twice
    ///     <see cref="Tokens" />, because a per-split cost that grows with the
    ///     token count must land in the measurement instead of hiding behind
    ///     the smaller command.
    /// </summary>
    private const int MeasuredTokens = Tokens * 2;

    /// <summary>Splits in one burst window.</summary>
    private const int CallsPerWindow = 200;

    /// <summary>
    ///     Splits per measured leg - the FIXED amount of work. 20 000 splits
    ///     is 100 burst windows over the 512-token command, about 66 ms of
    ///     real work on the CI runner.
    /// </summary>
    private const int SplitsPerLeg = 20_000;

    /// <summary>
    ///     Allocation budget for ONE split of the 512-token command, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    ///     Pinned from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 1 520 000 040 B over 40 000 splits = 38 000 B per
    ///     split. The budget leaves ~1.47x headroom.
    /// </summary>
    private const long MaxBytesPerSplit = 56_000;

    /// <summary>
    ///     Time budget for ONE split, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 260.250 ms over 40 000 splits = 6.51 us
    ///     per split against a probe that costs 0.000380 ms/unit, i.e. ~17 100
    ///     probe units per split. The budget leaves ~15x headroom, because the
    ///     probe's OWN measured cost moved 5.3x between two CI runs of the
    ///     same tree (0.000380 ms/unit in run 39423151864, 0.000072 ms/unit in
    ///     run 39436405496) - the BYTE budget is the branch that can fail on
    ///     any runner.
    /// </summary>
    private const double MaxProbeUnitsPerSplit = 250_000.0;

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
    ///     One measured leg: <paramref name="splits" /> splits over
    ///     <paramref name="command" />, timed and allocation-counter'd as one
    ///     window. The command is FIXED, so the budget is a per-split number
    ///     and the runner's load cannot move it.
    /// </summary>
    private static PerfGateLeg DriveSplit(string command, int splits)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        int bursts = splits / CallsPerWindow;
        for (int u = 0; u < bursts; u++)
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
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    ///     THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
    ///     to be <c>cost(2N)/cost(N) &lt;= 3</c> for both time and
    ///     allocations over a 256-token against a 512-token command, decided
    ///     by the shared TestScalingGate (#1652). It is now:
    ///     <code>
    ///     bytes per split  &lt;= 56 000    (machine-independent, HARD)
    ///     probe units/split &lt;= 250 000   (machine-scaled time, SOFT)
    ///     </code>
    ///     measured over a FIXED amount of work (20 000 splits of the
    ///     512-token command - the LARGER one, so a per-split cost that grows
    ///     with the token count lands in the measurement) on the SAME runner in
    ///     the SAME run. The allocation branch is machine-independent and is
    ///     asserted hard; the time branch is in PROBE UNITS and stands down
    ///     when the rounds do not reproduce each other. The report prints on
    ///     every run, pass or fail.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task Cost_PerSplit_StaysWithinBudget()
    {
        string small = BuildCommand(Tokens);
        string large = BuildCommand(MeasuredTokens);

        // Non-vacuity: the doubled command really tokenizes to twice the argv.
        await Assert.That(BashArgMatcher.SplitArgv(small).Count).IsEqualTo(Tokens + 1);
        await Assert.That(BashArgMatcher.SplitArgv(large).Count).IsEqualTo(MeasuredTokens + 1);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: splits => Task.FromResult(DriveSplit(large, splits)),
            units: SplitsPerLeg,
            options: new PerfGateOptions
            {
                Label = "bash-argv-cost",
                MaxBytesPerUnit = MaxBytesPerSplit,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerSplit,
            });

        await result.AssertWithinBudgetsAsync("bash argv split per split");
    }
}
