// EventStringShreddingGates.cs — #1139 (per-event string shredding, slice 3):
// the TUI event path of the CellForge renderer copied whole strings on paths
// that only needed a span, and the copies were paid per event, per frame.
//
// THE PREMISE (verified on origin/dev before the slice, not assumed)
// -------------------------------------------------------------------
//   * ToolCardTracker.EnsureCard(JsonElement) shaped the tool args (one
//     GetRawText + the summary) for EVERY ToolExecutionStart — but the LLM's
//     ToolCallStart opens the card first, and the string overload DISCARDS
//     the shaped args for a card that already exists. The copy was paid for a
//     result nobody reads.
//   * ToolCallBlock re-measured and re-painted every frame, and each of
//     those passes trimmed the tool output into a FRESH COPY of the whole
//     body (string.TrimEnd('\n') allocates when the output ends with a
//     newline — which a command's stdout always does). Three copies of a
//     64 KiB output per frame.
//   * HunkVirtualizer.SplitPatchHunks normalized the patch body twice
//     (Replace("\r\n") + Replace('\r')), split it into a string[] and then
//     Replace'd the tabs of every line — four copies of the body, on the
//     diff-preview panel's per-frame path.
//
// THE GATE (the #1747 discipline)
// -------------------------------
// A BUDGET PER UNIT OF WORK, not a ratio:
//
//     bytes per tool-start event     <= <budget>   (machine-independent, HARD)
//     bytes per painted frame        <= <budget>   (machine-independent, HARD)
//     bytes per patch split          <= <budget>   (machine-independent, HARD)
//     probe units per unit of work   <= <budget>   (machine-scaled time, SOFT)
//
// The byte branch is the load-bearing one: a byte count does not move with
// the runner's speed, so it is compared as measured, with no band. The time
// branch is written in PROBE UNITS — the cost of one unit of work divided by
// the cost of one unit of the fixed calibration probe measured on the SAME
// runner in the SAME run — so the runner's speed cancels and the budget is a
// constant in a machine-independent currency. It is asserted only when the
// measurement is trusted (reproducible spread); otherwise it reports and
// stands down.
//
// [NotInParallel] — bare, like ProjectHistoryScalingGates: the wall clock is
// not thread-local, and the allocation counter is read inside a synchronous
// window, so only the time leg needs the exclusivity. Both legs live in one
// test, so one attribute covers them.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

[NotInParallel]
public sealed class EventStringShreddingGates
{
    /// <summary>Tool-output bytes the frame gate paints (a big command body).</summary>
    private const int ToolOutputBytes = 64 * 1024;

    /// <summary>Lines the patch gate splits (a CRLF body with tabbed lines).</summary>
    private const int PatchLines = 1000;

    // ── Budgets: bytes per unit of work (machine-independent, HARD) ─────────
    //
    // Pinned from the CI measurement of the PRE-SLICE tree (run 39390539021,
    // PR #1910 — the gate landed before the fix, so that run measured the
    // shredding shape):
    //
    //   tool start   104.0 B/unit      (the args JSON + the summary)
    //   card frame   131 144.0 B/unit  (two copies of a 64 KiB body)
    //   patch split  175 248.0 B/unit  (two normalizations + the array + the
    //                                     per-line tab copies)
    //
    // Each budget sits BELOW the pre-slice cost (so the old shape fails the
    // gate) and ABOVE what the slice leaves behind (so the new shape passes
    // with headroom).

    /// <summary>
    ///     Byte budget for ONE tool-start event on a card the LLM already
    ///     opened. The shaped args are discarded for such a card, so the
    ///     honest budget is a handful of bytes — the dictionary probe.
    /// </summary>
    private const long MaxBytesPerToolStart = 8;

    /// <summary>
    ///     Byte budget for ONE painted frame carrying a 64 KiB tool card.
    ///     The frame paints spans; nothing of the body is copied.
    /// </summary>
    private const long MaxBytesPerFrame = 4 * 1024;

    /// <summary>
    ///     Byte budget for ONE split of a 1000-line patch. The split
    ///     materializes the lines themselves (that is the API's result), and
    ///     nothing else.
    /// </summary>
    private const long MaxBytesPerPatchSplit = 128 * 1024;

    // ── Time budgets: probe units per unit of work (machine-scaled, SOFT) ───

    /// <summary>Time budget for ONE tool-start event, in probe units.</summary>
    private const double MaxProbeUnitsPerToolStart = 1.0;

    /// <summary>Time budget for ONE painted frame, in probe units.</summary>
    private const double MaxProbeUnitsPerFrame = 700.0;

    /// <summary>Time budget for ONE patch split, in probe units.</summary>
    private const double MaxProbeUnitsPerPatchSplit = 1_400.0;

    /// <summary>
    ///     The floor below which a measured leg is not evidence. The units of
    ///     work here are small — a frame, a split — so the shared 5 ms default
    ///     would stand the time branch down on every run; 1 ms is the shortest
    ///     leg that still decides something on a hosted runner.
    /// </summary>
    private const double MinLegMs = 1.0;

    // ── The yardstick's calibration band ─────────────────────────────────────
    //
    // The time branch is written in PROBE UNITS: the cost of one unit of
    // work divided by the cost of one unit of the fixed calibration probe,
    // measured on the same runner in the same run. The probe is a FIXED
    // workload, so its cost is a property of the machine — and the machine
    // has a price: 0.000343-0.000353 ms/unit in the nineteen measured
    // `test (ui)` runs of the fleet that read it (the oldest, run
    // 39372950798, read 0.000380).
    //
    // Run 39609285153 read 0.000041 ms/unit — 8.5x below — while the workload
    // it was supposed to normalize was UNCHANGED (0.0816 ms/unit against
    // 0.0803-0.0827 across the same nineteen runs). The ratio was therefore
    // 8.5x off, the numerator's spread was 1.01x, the gate called the
    // measurement "trusted", and three gates failed the build on a number the
    // runner never produced.
    //
    // So the yardstick has to stay inside a band before its ratio is
    // believed. Outside the band the time branch reports and stands down,
    // and the BYTE branch — the machine-independent one, the branch that can
    // be a hard gate — still asserts.
    //
    // The band is wide on purpose: 2.3x below and 1.6x above what the fleet
    // produces, so a slower or a faster machine still measures inside it. A
    // fleet whose runner changes re-measures this band; the report line says
    // which way it went.
    private const double MinYardstickMsPerUnit = 0.00015;
    private const double MaxYardstickMsPerUnit = 0.00060;

    // ── Gate 1: the per-tool-call args shaping ──────────────────────────────

    /// <summary>Cards opened before the measured window (the LLM's path).</summary>
    private const int DistinctCards = 256;

    /// <summary>Tool starts driven per measured window.</summary>
    private const int ToolStartsPerWindow = 100_000;

    [Test]
    [Timeout(300_000)]
    public async Task ToolStart_On_An_Open_Card_CostsWithinBudget() =>
        await RunGate("tool-start event on an open card", ToolStartsPerWindow,
            Gate("tool-start-event-cost", MaxBytesPerToolStart, MaxProbeUnitsPerToolStart), DriveToolStartCost);

    /// <summary>
    ///     Drives one window: open every card first (the LLM's ToolCallStart
    ///     path), then measure the ToolExecutionStart that finds the card
    ///     there — the path that used to shape args nobody reads.
    /// </summary>
    private static PerfGateLeg DriveToolStartCost(int calls)
    {
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var tracker = new ToolCardTracker(panel);
        JsonElement args = JsonDocument.Parse("""{"cmd":"make build","path":"src/a.cs"}""").RootElement.Clone();

        // The ids are built OUTSIDE the measured window: an interpolated
        // string per iteration would be the measurement.
        var ids = new string[DistinctCards];
        for (int i = 0; i < DistinctCards; i++)
        {
            ids[i] = $"tc-{i}";
            tracker.EnsureCard(ids[i], "bash", argsSummary: null);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < calls; i++)
        {
            tracker.EnsureCard(ids[i % DistinctCards], "bash", args, restampStarted: true);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The window must have driven the path it claims to measure.
        if (!tracker.IsKnownCard(ids[0]))
        {
            throw new InvalidOperationException(
                $"Tool-start script drifted: the card {ids[0]} is not on the timeline after the window.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    // ── Gate 2: the per-frame tool-card paint ────────────────────────────────

    /// <summary>Frames driven per measured window.</summary>
    private const int FramesPerWindow = 32;

    [Test]
    [Timeout(300_000)]
    public async Task ToolCardFrame_CostsWithinBudget() =>
        await RunGate("painted frame carrying a 64 KiB tool card", FramesPerWindow,
            Gate("tool-card-frame-cost", MaxBytesPerFrame, MaxProbeUnitsPerFrame), DriveFrameCost);

    /// <summary>
    ///     Drives one window: a timeline carrying ONE completed tool card
    ///     whose body is 64 KiB, measured and painted every frame.
    /// </summary>
    private static PerfGateLeg DriveFrameCost(int frames)
    {
        var buffer = TestRender.Buffer(80, 24);
        var rect = new Rect(0, 0, 80, 20);
        var tl = new VirtualizedChatTimeline();
        var card = new ToolCallBlock(new ToolCallInfo("t1", "read", "src/a.cs")) { MaxBodyLines = 4 };
        card.Complete(new ToolResultBody(BuildToolOutput(ToolOutputBytes), isError: false, TimeSpan.FromMilliseconds(3)));
        tl.Append(card);

        // Warm past the tiered-JIT compilation of the measured path.
        for (int i = 0; i < 50; i++)
        {
            _ = tl.PrepareFrame(80, 20);
            tl.Paint(buffer, rect);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            _ = tl.PrepareFrame(80, 20);
            tl.Paint(buffer, rect);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The window must have painted the card, not an empty timeline.
        if (tl.Count != 1)
        {
            throw new InvalidOperationException(
                $"Frame script drifted: the timeline holds {tl.Count} block(s) instead of the one tool card.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     A tool body of <paramref name="bytes"/> bytes that ENDS WITH A
    ///     NEWLINE — the shape a command's stdout has, and the shape that
    ///     made <c>TrimEnd('\n')</c> copy the whole body.
    /// </summary>
    private static string BuildToolOutput(int bytes)
    {
        var sb = new StringBuilder(bytes + 64);
        int line = 0;
        while (sb.Length < bytes)
        {
            sb.Append("line ").Append(line).Append(" of the tool output\n");
            line++;
        }

        return sb.ToString();
    }

    // ── Gate 3: the per-frame patch split ────────────────────────────────────

    /// <summary>Patch splits driven per measured window.</summary>
    private const int PatchSplitsPerWindow = 8;

    [Test]
    [Timeout(300_000)]
    public async Task PatchSplit_CostsWithinBudget() =>
        await RunGate("split of a 1000-line CRLF patch", PatchSplitsPerWindow,
            Gate("patch-split-cost", MaxBytesPerPatchSplit, MaxProbeUnitsPerPatchSplit), DrivePatchSplitCost);

    /// <summary>
    ///     Drives one window: split the same patch body over and over. The
    ///     body is CRLF-authored and tabbed — the two shapes the old
    ///     normalize-then-split-then-replace chain paid for twice.
    /// </summary>
    private static PerfGateLeg DrivePatchSplitCost(int splits)
    {
        string patch = BuildPatch(PatchLines);

        // Warm past the tiered-JIT compilation of the measured path. The
        // call-counting threshold is 30 calls, and the measured window drives
        // only 8 splits per round, so a short warm-up leaves the first
        // measured rounds running tier-0 code and the median of the rounds
        // pays for it. 32 splits put the measured path on tier-1 before the
        // first measured round.
        for (int i = 0; i < 32; i++)
        {
            _ = HunkVirtualizer.SplitPatchHunks(patch);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        VirtualPatch? last = null;
        for (int i = 0; i < splits; i++)
        {
            last = HunkVirtualizer.SplitPatchHunks(patch);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The window must have split the body it claims to measure.
        if (last is null || last.TotalLines != PatchLines)
        {
            throw new InvalidOperationException(
                $"Patch script drifted: the last split carries {last?.TotalLines ?? -1} content line(s) "
                + $"instead of {PatchLines}.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>
    ///     A patch body with CRLF line endings and tabbed lines — the shape a
    ///     Windows-authored diff of tab-indented source has.
    /// </summary>
    private static string BuildPatch(int lines)
    {
        var sb = new StringBuilder();
        sb.Append("--- a/f.cs\r\n+++ b/f.cs\r\n");
        sb.Append("@@ -1,").Append(lines).Append(" +1,").Append(lines).Append(" @@\r\n");
        for (int i = 0; i < lines; i++)
        {
            // Mixed signs: an added-only run would be cut into virtual
            // chunks, which is a different path than the one measured here.
            sb.Append(i % 2 == 0 ? "+\tadded " : "-\tremoved ").Append(i).Append("\r\n");
        }

        return sb.ToString();
    }

    // ── The shared gate shape ────────────────────────────────────────────────

    /// <summary>
    ///     The budget shape every gate here shares: a constant per unit of
    ///     work, the allocation branch asserted hard and the time branch
    ///     asserted only when the measurement is reproducible.
    /// </summary>
    private static PerfGateOptions Gate(string label, long maxBytesPerUnit, double maxProbeUnitsPerUnit) =>
        new()
        {
            Label = label,
            MaxBytesPerUnit = maxBytesPerUnit,
            MaxProbeUnitsPerUnit = maxProbeUnitsPerUnit,

            // A workload that legitimately allocates nothing per unit opts
            // out of the positive-bytes floor: zero is the answer there, not
            // a missing measurement (#741/#591).
            RequirePositiveBytes = false,
            MinMeasurableLegMs = MinLegMs,
        };

    /// <summary>
    ///     Measure → report → assert. The report is printed before the
    ///     assertions run, so a red gate still leaves the numbers that
    ///     produced it in the log.
    /// </summary>
    private static async Task RunGate(
        string topic,
        int units,
        PerfGateOptions options,
        Func<int, PerfGateLeg> work)
    {
        PerfGateResult result = await PerfGate.MeasureAsync(
            work: u => Task.FromResult(work(u)),
            units: units,
            options: options);

        // THE YARDSTICK'S CALIBRATION. The time branch is written in probe
        // units: the cost of one unit of work divided by the cost of one unit
        // of the fixed calibration probe, measured on the same runner in the
        // same run. The probe is a FIXED workload, so its cost is a property
        // of the machine — and a yardstick that costs something other than
        // what it costs on a healthy runner is not measuring what it claims
        // to measure.
        //
        // The receipts: run 39633684054 read 0.000041 ms/unit — 8.5x below
        // the fleet's 0.000343-0.000353 — while the workload it was supposed
        // to normalize was UNCHANGED (0.0815 ms/unit against 0.0803-0.0827
        // across nineteen measured runs). The ratio was 8.5x off, the
        // numerator's spread was 1.01x, the helper called the measurement
        // "trusted", and three gates failed the build on a number the runner
        // never produced. Runs 39609285153 and 39619582982 read the same
        // 0.000041.
        //
        // Outside the band the ratio is not evidence about the code, so the
        // time branch REPORTS and stands down. The byte branch — the
        // machine-independent one, the branch that can be a hard gate — still
        // asserts: it is the real limit (the memory a tool card costs), and
        // it does not move with the runner's speed.
        if (result.ProbeMsPerUnit < MinYardstickMsPerUnit || result.ProbeMsPerUnit > MaxYardstickMsPerUnit)
        {
            result = result with
            {
                Trusted = false,
                TimeAsserted = false,
                TimeStandDownReason =
                    $"the yardstick was not calibrated on this runner (it cost {result.ProbeMsPerUnit:F6} ms/unit, "
                    + $"outside the calibration band {MinYardstickMsPerUnit:F6}-{MaxYardstickMsPerUnit:F6} ms/unit)",
            };
        }

        await result.AssertWithinBudgetsAsync(topic);
    }
}
