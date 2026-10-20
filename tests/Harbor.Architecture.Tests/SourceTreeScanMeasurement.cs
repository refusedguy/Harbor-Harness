// SourceTreeScanMeasurement.cs — the measurement behind issue #1791.
//
// WHAT THIS FILE IS
// -----------------
// The architecture suite was measured at 738.3 s of summed test duration
// (953 tests) against a 1128.5 s whole-suite total, and the hypothesis in the
// issue was that every rule keeps its OWN `static readonly Lazy<...>` copy of
// the source tree, so the tree is walked dozens of times per run.
//
// A hypothesis is not a measurement. This file MEASURES the three rules that
// account for the bulk of the suite's time, and it prints two different things
// for each of them:
//
//   1. the COST of the tree scan, split into the part every rule pays (read +
//      comment-strip) and the part only that rule pays (its own analysis); and
//   2. the FINDING COUNTS — how many hand-offs, orphans, writers, races the
//      rule sees on the current tree.
//
// (2) is the part that has to survive the change. A rule that stops finding
// its violations because the tree snapshot changed shape is a silent hole,
// and "the tests are still green" does not prove otherwise: a rule that finds
// nothing reports nothing, which is green. So the counts are printed with
// stable keys, and the same keys are printed by the same file BEFORE and
// AFTER the shared-snapshot change — the diff between the two runs is the
// parity proof.
//
// The output lines are picked up by `tools/test-measurements.py`, which renders
// every `Console.WriteLine` a test emits into the CI job summary. Nothing here
// is a gate: this file measures and reports, and the rules it measures keep
// their own assertions.
//
// THE TWO KINDS OF LINE
// ---------------------
//   [arch-count] — the parity contract. These lines must be IDENTICAL before
//                  and after the change. A difference means a rule's findings
//                  moved, and a rule whose findings moved has changed its
//                  verdict even if its test is still green.
//   [arch-cost]  — the measurement. These lines are expected to move, and
//                  moving them down is the point.

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures what the architecture suite's source-tree scanning costs and what
///     the three heaviest tree-scanning rules find on the current tree.
/// </summary>
/// <remarks>
///     <para>
///         The BARE <c>[NotInParallel]</c> is load-bearing, and for the same
///         reason it is load-bearing on the cost gates in
///         <c>Harbor.Abstractions.Tests</c> (#1790): a measurement that reads
///         wall-clock while a neighbouring test burns cores is measuring the
///         neighbour. The bare form is the only one that takes the
///         <c>NotInParallelLock</c>, so this class runs COMPLETELY ALONE —
///         which also means the per-test durations of every other rule in this
///         assembly are measured without this measurement distorting them, so
///         two runs of the same tree can be compared rule by rule.
///     </para>
///     <para>
///         The cost is that the run pays for this measurement serially, on top
///         of the suite. It is a measurement, and it is reported as one: its
///         own duration is one of the per-test durations in the TRX.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class SourceTreeScanMeasurement
{
    /// <summary>
    ///     The measurement. Prints one line per rule, with stable keys, so two
    ///     runs can be diffed line by line.
    /// </summary>
    [Test]
    public async Task Measure_Tree_Scan_Cost_And_Finding_Counts()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "every rule in this project reads the tree from RepoPaths.RepoRoot, and with no "
                + "root this measurement would report zeros that look like a clean tree.");

        MeasurePanelKeyRoute();
        MeasureProcessEnv();
        MeasureApprovalGate();
    }

    /// <summary>
    ///     The panel/overlay key hand-off rule: 200 s of the suite's 738 s, and
    ///     the only one of the three that already memoises its own scan.
    /// </summary>
    private static void MeasurePanelKeyRoute()
    {
        var watch = Stopwatch.StartNew();

        // The rule's OWN memoised state, so the measurement reports what the
        // rule sees rather than a second, parallel computation of it.
        KeyRouteReport report = PanelKeyRouteReachabilityRule.ProductReport;
        IReadOnlyList<SourceTreeFile> testSources = PanelKeyRouteReachabilityRule.TestSources;
        long readMs = watch.ElapsedMilliseconds;

        watch.Restart();
        IReadOnlyList<string> deadRouteTests =
            PanelKeyRouteProbe.TestsThatDriveOnlyDeadRoutes(report, testSources);
        long deadTestMs = watch.ElapsedMilliseconds;

        PanelKeyRouteProbe.PartitionDebt(
            report,
            out IReadOnlyList<OrphanHandOff> undeclared,
            out IReadOnlyList<AcknowledgedDebt> stale);

        Console.WriteLine(
            $"[arch-count] panel-key-route files={report.FilesScanned} handoffs={report.HandOffs.Count} "
            + $"orphans={report.Orphans.Count} undeclared={undeclared.Count} stale={stale.Count} "
            + $"dead-route-tests={deadRouteTests.Count}");

        Console.WriteLine(
            $"[arch-cost] panel-key-route read-ms={readMs} dead-test-ms={deadTestMs}");
    }

    /// <summary>
    ///     The process-environment rule: 173 s of the suite's 738 s, and the
    ///     only one of the three that did NOT memoise its discovery — so it
    ///     re-parsed the whole test tree once per test.
    /// </summary>
    private static void MeasureProcessEnv()
    {
        var watch = Stopwatch.StartNew();
        IReadOnlyList<ProcessEnvIsolationRule.TypeSite> writers = ProcessEnvIsolationRule.DiscoverWriters();
        long discoverMs = watch.ElapsedMilliseconds;

        int written = writers.Sum(s => s.WrittenVariables.Length);
        int restored = writers.Count(s =>
            s.WrittenVariables.Length > 0
            && s.WrittenVariables.All(v => s.RestoredVariables.Contains(v, StringComparer.Ordinal)));
        int projects = writers.Select(s => s.Project).Distinct(StringComparer.Ordinal).Count();

        Console.WriteLine(
            $"[arch-count] process-env writers={writers.Count} restored={restored} "
            + $"written-variables={written} projects={projects}");

        Console.WriteLine($"[arch-cost] process-env discover-ms={discoverMs}");
    }

    /// <summary>
    ///     The approval-gate waiter race rule: 50 s of the suite's 738 s, and
    ///     it re-walked and re-scanned the test tree once per test as well.
    /// </summary>
    private static void MeasureApprovalGate()
    {
        var watch = Stopwatch.StartNew();
        IReadOnlyList<string> files = ApprovalGateWaiterRaceRules.EnumerateTestSources();
        long enumerateMs = watch.ElapsedMilliseconds;

        watch.Restart();
        IReadOnlyList<(string File, ApprovalGateWaiterRaceRules.WaiterRace Race)> scan =
            ApprovalGateWaiterRaceRules.ScanTestTree();
        long scanMs = watch.ElapsedMilliseconds;

        // The subject of the rule, counted so the scan's reach is visible: a
        // rule that saw no waiter call at all would report zero races, and
        // that zero would be indistinguishable from a clean tree.
        int subjects = 0;
        foreach (string file in files)
        {
            SourceTreeFile? source = SharedSourceTreeSnapshot.Shared.Get(file);
            if (source is null)
            {
                continue;
            }

            int index = source.Text.IndexOf("WaitForDecisionAsync", StringComparison.Ordinal);
            while (index >= 0)
            {
                subjects++;
                index = source.Text.IndexOf("WaitForDecisionAsync", index + 1, StringComparison.Ordinal);
            }
        }

        Console.WriteLine(
            $"[arch-count] approval-gate files={files.Count} races={scan.Count} "
            + $"waiter-subjects={subjects}");

        Console.WriteLine(
            $"[arch-cost] approval-gate enumerate-ms={enumerateMs} scan-ms={scanMs}");
    }
}
