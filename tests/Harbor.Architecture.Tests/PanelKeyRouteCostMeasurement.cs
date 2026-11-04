// PanelKeyRouteCostMeasurement.cs — what the panel/overlay key-route rule
// costs, and what each part of it costs.
//
// WHY THIS FILE EXISTS
// --------------------
// `PanelKeyRouteReachabilityRule` holds the `arch` shard's second-longest
// class span: 47.0–49.0 s after #2156, against 84.0 s on dev. A span is a
// WALL number, and it does not say which part of the rule made it. This file
// is the decomposition, and it prints two kinds of line:
//
//   [arch-cost]  the cost. These lines are expected to move, and moving them
//                DOWN is the point.
//   [arch-count] the shape of the corpus. These lines say how much was
//                measured, so a number cannot be read without knowing what it
//                was measured over.
//
// TWO NUMBERS FOR THE SAME SCAN, AND WHY BOTH ARE HERE
// -----------------------------------------------------
// The rule memoises its scan, so the scan happens ONCE — in whichever thread
// asks for it first, under the shard's real contention. That scan's phases are
// recorded INSIDE it (`PanelKeyRouteProbe.LastPhases`), and those are the
// numbers the class span is made of.
//
// This measurement ALSO runs the scan's parts on its own, alone, with nothing
// else running. The difference between the two is the contention, and both are
// needed: the in-situ numbers say what the rule costs the shard, and the
// isolated numbers say what the rule costs the CPU.
//
// The ORACLE — the whole-file match collection the rule used to enumerate, and
// the map the rule's verdicts were computed with — is measured by
// `PanelKeyRouteEquivalenceTests`, which builds it to compare it. This
// measurement must not build it too, or the proof would be paid twice.
//
// WHY IT IS ITS OWN CLASS, AND WHY IT IS [NotInParallel]
// ------------------------------------------------------
// The bare `[NotInParallel]` is load-bearing for the same reason it is
// load-bearing in `SourceTreeScanMeasurement`: a measurement that reads the
// wall clock while a neighbouring test burns cores is measuring the
// neighbour. The bare form takes the `NotInParallelLock`, so this class runs
// COMPLETELY ALONE — which also means the per-test durations of every other
// rule in this assembly are measured without this measurement distorting them.
//
// The allocations are the thread's own, read with
// `GC.GetAllocatedBytesForCurrentThread()`: the work is single-threaded here,
// so the number is the work's.

using System.Diagnostics;
using System.Text;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures what the panel/overlay key-route rule costs over the live tree,
///     split into the phases the rule is made of.
/// </summary>
[NotInParallel]
public sealed class PanelKeyRouteCostMeasurement
{
    /// <summary>The measurement.</summary>
    [Test]
    public async Task The_Key_Route_Rule_Cost_Is_Measured()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "every rule in this project reads the tree from RepoPaths.RepoRoot, and with no "
                + "root this measurement would report zeros that look like a clean tree.");

        var summary = new StringBuilder();

        // ── the scan the rule actually did, under the shard's contention ─────
        //
        // Touching the rule's memoised report makes sure the scan has happened
        // at all; it has usually happened already, because the rule's own tests
        // ran before this measurement (this class is [NotInParallel], so it
        // runs after the parallel phase).
        _ = PanelKeyRouteReachabilityRule.ProductReport;

        ScanPhases? phases = PanelKeyRouteProbe.LastPhases;

        if (phases is { } seen)
        {
            Line(summary,
                $"[arch-cost] panel-key-route insitu read-ms={seen.ReadMs} "
                + $"type-map-ms={seen.TypeMapMs} handoff-scan-ms={seen.HandOffScanMs} "
                + $"callers-ms={seen.CallersMs} total-ms={seen.TotalMs}");

            Line(summary,
                $"[arch-count] panel-key-route insitu files={seen.Files} lines={seen.Lines} "
                + $"handoffs={seen.HandOffSites} enclosing-walks={seen.EnclosingWalks} "
                + $"callers-passes={seen.CallersPasses} member-matches={seen.MemberMatches} "
                + $"callsite-matches={seen.CallSiteMatches} handoff-matches={seen.HandOffMatches}");
        }

        IReadOnlyList<SourceTreeFile> sources = PanelKeyRouteProbe.ReadProductTrees();

        // ── the type→file map, the way the rule builds it ──────────────────
        //
        // The map is the phase the scan spent its wall on, so its cost is the
        // number that says whether the rule got faster.
        long beforeMap = GC.GetAllocatedBytesForCurrentThread();
        var mapWatch = Stopwatch.StartNew();
        Dictionary<string, List<string>> map =
            PanelKeyRouteProbe.BuildTypeOwner(sources, TypeScanStrategy.PerLine);
        mapWatch.Stop();
        long mapBytes = GC.GetAllocatedBytesForCurrentThread() - beforeMap;

        Line(summary,
            $"[arch-cost] panel-key-route type-map strategy=PerLine "
            + $"ms={mapWatch.ElapsedMilliseconds} bytes={mapBytes} types={map.Count}");

        // The scan WITHOUT the map: what the rest of the rule costs.
        var scanWatch = Stopwatch.StartNew();
        KeyRouteReport report = PanelKeyRouteProbe.ScanWithTypeOwner(sources, map);
        scanWatch.Stop();

        Line(summary,
            $"[arch-cost] panel-key-route isolated-scan-without-map-ms={scanWatch.ElapsedMilliseconds} "
            + $"handoffs={report.HandOffs.Count} orphans={report.Orphans.Count}");

        // ── each pattern of the scan, alone, over the whole tree ───────────
        ScanComponentCosts components = PanelKeyRouteProbe.MeasureComponents(sources);

        Line(summary,
            $"[arch-cost] panel-key-route pattern member ms={components.MemberPatternMs} "
            + $"calls={components.MemberPatternCalls}");

        Line(summary,
            $"[arch-cost] panel-key-route pattern handoff ms={components.HandOffPatternMs} "
            + $"calls={components.HandOffPatternCalls}");

        Line(summary,
            $"[arch-cost] panel-key-route pattern callsite ms={components.CallSitePatternMs} "
            + $"calls={components.CallSitePatternCalls}");

        Line(summary,
            $"[arch-cost] panel-key-route pattern type ms={components.TypePatternMs} "
            + $"calls={components.TypePatternCalls}");

        Line(summary,
            $"[arch-cost] panel-key-route cold-read ms={components.ColdReadMs} "
            + $"chars={components.ColdReadChars} files={components.Files} lines={components.Lines}");

        Line(summary,
            $"[arch-cost] panel-key-route cold-walk ms={components.ColdWalkMs} "
            + $"files={components.ColdWalkFiles}");

        // ── the test tree, which the rule's measurement also reads ─────────
        IReadOnlyList<SourceTreeFile> testSources = PanelKeyRouteReachabilityRule.TestSources;

        long beforeTest = GC.GetAllocatedBytesForCurrentThread();
        var deadWatch = Stopwatch.StartNew();
        IReadOnlyList<string> deadRouteTests =
            PanelKeyRouteProbe.TestsThatDriveOnlyDeadRoutes(report, testSources);
        deadWatch.Stop();
        long deadTestBytes = GC.GetAllocatedBytesForCurrentThread() - beforeTest;

        Line(summary,
            $"[arch-cost] panel-key-route isolated-dead-test-ms={deadWatch.ElapsedMilliseconds} "
            + $"bytes={deadTestBytes} hits={deadRouteTests.Count} "
            + $"test-files={testSources.Count}");

        await Assert.That(summary.Length).IsGreaterThan(0)
            .Because("a measurement that printed nothing measured nothing.");
    }

    /// <summary>Prints one line and keeps it in the summary.</summary>
    /// <param name="summary">The summary being built.</param>
    /// <param name="line">The line to print and to keep.</param>
    private static void Line(StringBuilder summary, string line)
    {
        Console.WriteLine(line);
        summary.AppendLine(line);
    }
}
