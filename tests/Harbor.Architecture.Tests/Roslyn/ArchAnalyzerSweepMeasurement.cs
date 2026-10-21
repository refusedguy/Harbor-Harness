// ArchAnalyzerSweepMeasurement.cs — what the ONE analyzer sweep costs, and
// the proof that there is only one.
//
// WHY THIS FILE EXISTS
// --------------------
// The Roslyn harness used to run the analyzer driver over the same 64
// product compilations FOUR times: the differential test wanted HARCH001's
// file list, the namespace test wanted HARCH001's file list AGAIN and
// HARCH002's findings, and the unguarded-Result test wanted HARCH003's
// sites. Each of those was a full driver pass over every product project,
// and the measured cost of ONE of them is 77 s (#1791's measurement, quoted
// in docs/BENCHMARKS.md §6.1).
//
// The sweep is now ONE memoised pass carrying all three analyzers, and the
// compilations are swept CONCURRENTLY, bounded by the core count.
//
// WHAT IS PROVED HERE
// -------------------
//   * the sweep RAN ONCE: `ArchAnalyzerPromotionTests.SweepRuns` counts the
//     passes, and a number that is not 1 means some caller ran the sweep on
//     its own and the cost is back;
//   * the sweep is not vacuous: it found the product compilations, and the
//     analyzers' findings are reported.
//
// The cost is printed, not asserted: a measurement that fails a build on a
// slow runner is a gate that gets switched off.

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures the ONE analyzer sweep over the product tree, and proves that
///     there is only one.
/// </summary>
[NotInParallel]
public sealed class ArchAnalyzerSweepMeasurement
{
    /// <summary>The measurement, and the proof.</summary>
    [Test]
    public async Task The_Analyzer_Sweep_Runs_Once_And_Its_Cost_Is_Measured()
    {
        var watch = Stopwatch.StartNew();
        ArchAnalyzerPromotionTests.LiveAnalyzerSweep sweep =
            await ArchAnalyzerPromotionTests.LiveAnalyzerSweepResult.Value;
        watch.Stop();

        int compilations = ArchAnalyzerPromotionTests.LiveCompilations.Value.Count;
        int passes = ArchAnalyzerPromotionTests.SweepRuns;

        Console.WriteLine(
            $"[arch-cost] analyzer-sweep compilations={compilations} passes={passes} "
            + $"ms={watch.ElapsedMilliseconds} harch001={sweep.DynamicCode.Length} "
            + $"harch002={sweep.NamespaceOwnership.Length} harch003={sweep.UnguardedResult.Length}");

        await Assert.That(compilations).IsGreaterThan(0)
            .Because(
                "the sweep ran over nothing: a driver pass over zero product compilations proves "
                + "nothing about the product tree, and the number it reports is the cost of doing "
                + "nothing.");

        await Assert.That(passes).IsEqualTo(1)
            .Because(
                "the sweep is the ONE expensive thing this family does, and it used to be done FOUR "
                + "times over the same 64 compilations. A number above one means a caller ran the "
                + "sweep on its own, and the cost of the family is back to four passes. Passes: "
                + passes);
    }
}
