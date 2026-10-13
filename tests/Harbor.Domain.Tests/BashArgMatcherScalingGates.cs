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
// the runner, not the code.
[NotInParallel]
public sealed class BashArgMatcherScalingGates
{
    private const int Tokens = 256;
    private const double GrowthLimit = 3.0;
    private const int Rounds = 5;
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

    private static (TimeSpan Elapsed, long Allocated) DriveSplit(string command)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < CallsPerWindow; i++)
        {
            var argv = BashArgMatcher.SplitArgv(command);
            if (argv.Count == 0)
                        throw new InvalidOperationException("SplitArgv measured nothing.");
        }
        sw.Stop();
        return (sw.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
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

        // Warm-up (tiered JIT), discarded.
        _ = DriveSplit(small);

        long allocSmall = long.MaxValue;
        long allocLarge = long.MaxValue;
        double msSmall = double.MaxValue;
        double msLarge = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            var s = DriveSplit(small);
            var l = DriveSplit(large);
            allocSmall = Math.Min(allocSmall, s.Allocated);
            allocLarge = Math.Min(allocLarge, l.Allocated);
            msSmall = Math.Min(msSmall, s.Elapsed.TotalMilliseconds);
            msLarge = Math.Min(msLarge, l.Elapsed.TotalMilliseconds);
        }

        double allocRatio = (double)allocLarge / allocSmall;
        double timeRatio = msLarge / msSmall;

        Console.WriteLine(
            $"bash-argv-scaling: {Tokens} tokens = {msSmall:F3} ms / {allocSmall / 1024.0:F1} KiB, "
            + $"{Tokens * 2} tokens = {msLarge:F3} ms / {allocLarge / 1024.0:F1} KiB "
            + $"(time x{timeRatio:F2}, alloc x{allocRatio:F2}, limit x{GrowthLimit:F2}, best of {Rounds}; "
            + "the absolute columns are informational and runner-dependent)");

        await Assert.That(allocSmall)
            .IsGreaterThan(0L)
            .Because("the allocation baseline must be a real measurement: allocSmall = " + allocSmall + " B.");
        await Assert.That(msSmall)
            .IsGreaterThan(0.0)
            .Because("the time baseline must be a real measurement: msSmall = " + msSmall.ToString("F3") + " ms.");

        await Assert.That(timeRatio)
            .IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                "time(2N)/time(N) = " + timeRatio.ToString("F2") + " against a limit of "
                + GrowthLimit.ToString("F2") + ". Linear is 2.00 and O(N^2) is 4.00.");
        await Assert.That(allocRatio)
            .IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                "alloc(2N)/alloc(N) = " + allocRatio.ToString("F2") + " against a limit of "
                + GrowthLimit.ToString("F2") + ".");
    }
}
