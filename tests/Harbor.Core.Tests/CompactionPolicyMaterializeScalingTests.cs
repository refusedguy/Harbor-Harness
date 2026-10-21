// CompactionPolicyMaterializeScalingTests.cs — #1533, the RELATIVE gate over the
// per-turn materialize cycle ('#97' slice).
//
// The claim under test: CompactionPolicy.MaterializeCompactedView used to pay
// TWO O(history) scans on every turn — the reverse scan for the newest summary
// plus the forward anchor walk, twice per turn — even when NOTHING was
// compacted (#1533: TurnRunner.cs:232 for the reverse scan and
// CompactionBehavior.cs:230 after a fresh compaction). Both files moved to
// Harbor.AgentRuntime by #1623. After #1533 the anchor is remembered per input list: an
// unchanged history returns the remembered answer with zero rescans and zero
// allocations (still the input instance itself when there is no summary, so
// the documented fail-safe identity is untouched); appended history rescans
// only the appended window.
//
// The GATE is the #410/#1528 convention, now ridden on the shared
// TestScalingGate skeleton (#1652): N against 2N inside one run, no absolute
// budgets. Two legs shapes, because the materialize cost has two halves:
//
//  - NO-SUMMARY history (the dominant real-world session): the unit is one
//    TURN CYCLE — append one message to the session history and call
//    MaterializeCompactedView the two times a turn calls it. The small leg
//    grows a history from EMPTY through N cycles (it ends holding N
//    messages); the large leg starts from a prebuilt N-message base and runs
//    the SAME N cycles (it ends holding 2N messages). The pre-#1533 scan was
//    linear in the live history, so its whole run was quadratic: ratio 3.00.
//    The cached shape rescans only the appended window: linear, ratio 2.00.
//
//  - SUMMARY-ANCHORED history: the materialize view is REBUILT per turn (the
//    per-turn copy is the historical cost, kept — ownership of a shared
//    mutable view list would trade the scan for a race, §OOP-001), so a
//    growing-legs shape cannot separate the scan from the copy. This shape
//    is measured at a FIXED history instead: the small leg materializes an
//    N-message summarized history N times, the large leg a 2N-message one.
//    That is exactly the within-turn repeat call (#97: "O(n) twice per
//    turn"): the FIRST call of each leg pays the full scan + view, every
//    subsequent call rides the anchor cache.
//
//     t(2N) <= 3 * t(N)     (time, plus the #1652 1.05 band)
//     alloc(2N) <= 3 * alloc(N)   (allocations, strict)
//
// Linear is 2.00 and O(history-per-turn) is 3.00 on these legs; the 3.0
// GrowthLimit leaves headroom over the ideal while still catching the
// per-turn O(history) shape. The absolute milliseconds a leg happened to
// produce are printed for information (they feed docs/BENCHMARKS.md) and are
// NOT what any gate reads — same non-convention as the #410 rows there.
//
// The pre-#1533 shape runs as a REPORT-ONLY companion gate (a private,
// clearly labelled copy of the original scan code) so the BENCHMARKS.md
// before/after rows are measured in the SAME CI run on the same machine. It
// is never asserted: an old shape that a rerun measures at exactly the 3.0
// linear-history rate is a true statement about history, not a red gate.

using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using Harbor.TestKit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Core.Tests;

[NotInParallel("materialize-scale")]
public class CompactionPolicyMaterializeScalingTests
{
    /// <summary>Where the leg-scaling probe starts: a few thousand turn cycles.</summary>
    private const int InitialTurnCycles = 25_000;

    /// <summary>
    ///     Hard ceiling for the fresh-shape probe. Each cycle costs well under a
    ///     microsecond, so this is what lets one leg reach the
    ///     <see cref="TestScalingGateOptions.TargetLegMs" /> of 100 ms.
    /// </summary>
    private const int MaxTurnCycles = 250_000;

    /// <summary>Where the fixed-history (summarized shape) probe starts.</summary>
    private const int InitialSteadyHistory = 4_000;

    /// <summary>Hard ceiling for the fixed-history probe: a 2N prebuilt history per leg round.</summary>
    private const int MaxSteadyHistory = 100_000;

    /// <summary>
    ///     Probe bounds for the REPORT-ONLY pre-#1533 shape: its per-cycle cost
    ///     is O(history), so the growing leg time grows quadratically in
    ///     cycles — the probe must stop near 100 ms legs without ever scaling
    ///     to the fresh-shape budget, or one round would cost tens of seconds.
    /// </summary>
    private const int LegacyInitialCycles = 256;
    private const int LegacyMaxCycles = 4_096;

    /// <summary>Report-only bounds for the fixed-history shape; same reasoning.</summary>
    private const int LegacyInitialSteadyHistory = 512;
    private const int LegacyMaxSteadyHistory = 8_192;

    /// <summary>
    ///     The dominating real-world shape: a session that has never compacted.
    ///     Every materialize was a full reverse scan that found no summary and
    ///     returned the input unchanged — the hot path #1533 is about.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task TurnCycle_NoSummaryHistory_MaterializeCost_DoesNotScaleWithHistory()
    {
        TestScalingGateResult fresh = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(MeasureFreshLeg(units, preGrown: false)),
            measureLargeLeg: units => Task.FromResult(MeasureFreshLeg(units, preGrown: true)),
            initialUnits: InitialTurnCycles,
            options: new TestScalingGateOptions
            {
                Label = "materialize-turn-cycle#1533(no-summary)",
                MaxUnits = MaxTurnCycles,
            });

        await fresh.AssertWithinLimitsAsync(
            "turn-cycle MaterializeCompactedView(2N-history)/turn-cycle MaterializeCompactedView(N-history)",
            "Linear is 2.00; the pre-#1533 per-turn O(history) double scan is 3.00 on these legs.");

        // Non-vacuity: the legs really drove turns.
        List<AgentMessage> history = BuildBaseHistory(0);
        int expected = 0;
        for (int i = 0; i < 10; i++)
        {
            history.Add(MessageFixtures.User($"gate-cycle-{i}"));
            expected = history.Count;
            IReadOnlyList<AgentMessage> view = CompactionPolicy.MaterializeCompactedView(history);
            await Assert.That(view.Count).IsEqualTo(expected);
            await Assert.That(ReferenceEquals(view, history)).IsTrue()
                .Because("a no-summary history is the fail-safe arm: the input is returned unchanged");
        }

        TestScalingGateResult legacy = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(MeasureLegacyLeg(units, preGrown: false)),
            measureLargeLeg: units => Task.FromResult(MeasureLegacyLeg(units, preGrown: true)),
            initialUnits: LegacyInitialCycles,
            options: new TestScalingGateOptions
            {
                Label = "materialize-turn-cycle#1533-legacy(no-summary)",
                MaxUnits = LegacyMaxCycles,
            });

        TestContext.Current?.Output.WriteLine(legacy.Report);
    }

    /// <summary>
    ///     The summary-anchored shape, measured at a FIXED history: the small
    ///     leg materializes an N-message summarized history N times, the large
    ///     leg a 2N-message history the same number of times — exactly the
    ///     within-turn repeat call (TurnRunner's materialize, then
    ///     CompactionBehavior's). The first call of each leg pays the scan plus
    ///     the view build; every later call rides the anchor cache and must be
    ///     independent of the history length. The growing shape deliberately
    ///     has no gate here: the per-turn view REBUILD is history-linear even
    ///     after #1533 (the old scan cost removed, the copy kept), which would
    ///     pin the ratio at the 3.0 boundary — a fair statement about history,
    ///     not a usable gate.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task RepeatCallAtFixedSummarizedHistory_MaterializeCost_DoesNotScaleWithHistory()
    {
        TestScalingGateResult fresh = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(MeasureSteadyLeg(units, doubled: false)),
            measureLargeLeg: units => Task.FromResult(MeasureSteadyLeg(units, doubled: true)),
            initialUnits: InitialSteadyHistory,
            options: new TestScalingGateOptions
            {
                Label = "materialize-repeat-call#1533(summarized)",
                MaxUnits = MaxSteadyHistory,
            });

        await fresh.AssertWithinLimitsAsync(
            "repeat MaterializeCompactedView(2N summarized)/repeat MaterializeCompactedView(N summarized)",
            "Linear is 2.00; per-call O(history) scanning machinery lands at 2.00-ish, "
            + "the cached repeat at ~1.00.");

        // Non-vacuity: the summarized fast path really returned the
        // summary-first view with every call, on a history nobody appended to.
        (List<AgentMessage> history, AgentMessage summary) = BuildSummarizedHistory(3);
        for (int i = 0; i < 10; i++)
        {
            IReadOnlyList<AgentMessage> view = CompactionPolicy.MaterializeCompactedView(history);

            await Assert.That(view[0].Id).IsEqualTo(summary.Id)
                .Because("the compacted view opens on the summary, no matter how often it is recomputed");
            // The view drops the three folded seeds (the kept tail is the
            // anchor seed only), hence count minus two at every repeat call.
            await Assert.That(view.Count).IsEqualTo(history.Count - 2);
        }

        // REPORT-ONLY: pre-#1533 shape over the same fixed histories. Feeds
        // docs/BENCHMARKS.md, never gates.
        TestScalingGateResult legacy = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(MeasureSteadyLegacyLeg(units, doubled: false)),
            measureLargeLeg: units => Task.FromResult(MeasureSteadyLegacyLeg(units, doubled: true)),
            initialUnits: LegacyInitialSteadyHistory,
            options: new TestScalingGateOptions
            {
                Label = "materialize-repeat-call#1533-legacy(summarized)",
                MaxUnits = LegacyMaxSteadyHistory,
            });

        TestContext.Current?.Output.WriteLine(legacy.Report);
    }

    // ---- the legs -----------------------------------------------------

    /// <summary>
    ///     One measured fresh-shape leg: <paramref name="preGrown" /> chooses
    ///     the N-cycle history over an EMPTY base (small leg) or over a
    ///     prebuilt N-message base (large leg, 2N at the end). The measured
    ///     window carries only the turn cycles — the base build and the append
    ///     pool are prepared outside it.
    /// </summary>
    private static TestScalingGateLeg MeasureFreshLeg(int units, bool preGrown)
    {
        List<AgentMessage> history = BuildBaseHistory(preGrown ? units : 0);
        AgentMessage[] turnPool = BuildTurnPool(units, summarized: false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int cycle = 0; cycle < units; cycle++)
        {
            history.Add(turnPool[cycle]);
            _ = CompactionPolicy.MaterializeCompactedView(history);
            _ = CompactionPolicy.MaterializeCompactedView(history);
        }
        sw.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     One measured fixed-history leg: a summarized history of
    ///     <paramref name="units" /> (small) or <c>2·units</c> (large) messages,
    ///     materialized <paramref name="units" /> times against the SAME list —
    ///     one full pass, every repeat on the cache.
    /// </summary>
    private static TestScalingGateLeg MeasureSteadyLeg(int units, bool doubled)
    {
        List<AgentMessage> history = BuildSummarizedHistory(doubled ? units * 2 : units).History;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int call = 0; call < units; call++)
        {
            _ = CompactionPolicy.MaterializeCompactedView(history);
        }
        sw.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     The same fixed-history leg driven through a private copy of the
    ///     ORIGINAL scan shape (reverse summary scan + forward anchor walk +
    ///     fresh view build, exactly pre-#1533) — the BEFORE half of the
    ///     docs/BENCHMARKS.md rows, measured in the same run. Report-only;
    ///     nothing here is a gate.
    /// </summary>
    private static TestScalingGateLeg MeasureSteadyLegacyLeg(int units, bool doubled)
    {
        List<AgentMessage> history = BuildSummarizedHistory(doubled ? units * 2 : units).History;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int call = 0; call < units; call++)
        {
            _ = Pre1533Materialize(history);
        }
        sw.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     The growing no-summary leg driven through the same ORIGINAL scan
    ///     shape — the BEFORE half of the docs/BENCHMARKS.md rows, measured in
    ///     the same run. Report-only; nothing here is a gate.
    /// </summary>
    private static TestScalingGateLeg MeasureLegacyLeg(int units, bool preGrown)
    {
        List<AgentMessage> history = BuildBaseHistory(preGrown ? units : 0);
        AgentMessage[] turnPool = BuildTurnPool(units, summarized: false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int cycle = 0; cycle < units; cycle++)
        {
            history.Add(turnPool[cycle]);
            _ = Pre1533Materialize(history);
            _ = Pre1533Materialize(history);
        }
        sw.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     Verbatim pre-#1533 body of <c>MaterializeCompactedView</c> — kept as
    ///     data for the report-only legacy legs above, never for product code.
    /// </summary>
    private static IReadOnlyList<AgentMessage> Pre1533Materialize(IReadOnlyList<AgentMessage> messages)
    {
        int summaryIndex = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage { IsSummary: true })
            {
                summaryIndex = i;
                break;
            }
        }

        if (summaryIndex < 0)
        {
            return messages;
        }

        var summary = (AssistantMessage)messages[summaryIndex];
        int keptStart = summaryIndex;
        if (summary.SummaryFirstKeptId is string anchor)
        {
            bool resolved = false;
            for (int i = 0; i < summaryIndex; i++)
            {
                if (string.Equals(messages[i].Id, anchor, StringComparison.Ordinal))
                {
                    keptStart = i;
                    resolved = true;
                    break;
                }
            }

            if (!resolved)
            {
                return messages;
            }
        }

        var view = new List<AgentMessage>(1 + summaryIndex - keptStart + (messages.Count - summaryIndex - 1));
        view.Add(summary);
        for (int i = keptStart; i < summaryIndex; i++)
        {
            view.Add(messages[i]);
        }
        for (int i = summaryIndex + 1; i < messages.Count; i++)
        {
            view.Add(messages[i]);
        }

        return view;
    }

    // ---- fixtures -----------------------------------------------------

    /// <summary>A turn-message pool, one message per cycle, built outside the measured window.</summary>
    private static AgentMessage[] BuildTurnPool(int units, bool summarized)
    {
        var pool = new AgentMessage[units];
        for (int i = 0; i < units; i++)
        {
            pool[i] = summarized ? MessageFixtures.Assistant($"turn-{i}") : MessageFixtures.User($"turn-{i}");
        }

        return pool;
    }

    /// <summary>A plain (never-compacted) history of <paramref name="count" /> messages.</summary>
    private static List<AgentMessage> BuildBaseHistory(int count)
    {
        var history = new List<AgentMessage>(count);
        for (int i = 0; i < count; i++)
        {
            history.Add(MessageFixtures.User($"seed-{i}"));
        }

        return history;
    }

    /// <summary>
    ///     A summarized history: <paramref name="count" /> seed messages, then
    ///     the summary that folds them in with its anchor on the last seed —
    ///     the exact layout a real compaction appends.
    /// </summary>
    private static (List<AgentMessage> History, AgentMessage Summary) BuildSummarizedHistory(int count)
    {
        var history = new List<AgentMessage>(count + 1);
        string anchorId = string.Empty;
        for (int i = 0; i < count; i++)
        {
            var seed = MessageFixtures.User($"seed-{i}");
            anchorId = seed.Id;
            history.Add(seed);
        }

        var summary = MessageFixtures.Summary("folded history", anchorId);
        history.Add(summary);
        return (history, summary);
    }

    // ---- message fixtures (same shape as the invariant tests) ---------

}
