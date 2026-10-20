// ProjectHistoryScalingGates.cs — #1535 (hot-path slice of #97, per-fold):
// DefaultUiProjector.ProjectHistory rebuilt a Dictionary<ChatLine,int> first-
// occurrence map over the WHOLE transcript on every history change — one
// dictionary allocation plus N hash inserts per fold, per frame batch — even
// when the change was a pure append (the MessageEnd fold, the common case),
// whose unchanged prefix the map already covered.
//
// The map is now cached against `cache.Lines` (the same key the
// BaseRendered/BaseBlocks prefix reuse is keyed on) and extended over only the
// appended lines; the full rebuild stays for a changed prefix, where entries
// left over from beyond the prefix would return stale first-occurrence
// indices. The first-equal-wins BlockId semantics (the historical
// `Lines.IndexOf(line)` shape) is unchanged — `BlockIds_KeepFirstEqualWins_`
// pins it against a reference mapper on both paths.
//
// GATE (rewritten by #1747): a BUDGET PER UNIT OF WORK, not a ratio.
//
// It used to be `cost(2N)/cost(N) <= 3` for time and for allocations, over
// 512 against 1024 transcript lines, decided from best-of-5 legs with no GC
// fence and no leg scaling. That is the shape the #1747 inventory names as
// the receipt for "a ratio depends on the base and on the noise": this gate
// reddened on a byte-identical tree at **time x3.10 while alloc x1.99**
// (#1285, run 38129838108) — the time leg overran its limit while the
// allocation leg, which does not move with the runner at all, said the tree
// was fine.
//
// The gate is now:
//
//     bytes per append+project pair  <= 192 KiB   (machine-independent, HARD)
//     probe units per pair           <= 4000      (machine-scaled time, SOFT)
//
// measured over a FIXED amount of work (128 append+project pairs) at the
// LARGER transcript (1024 lines), so a path that costs more per change the
// bigger the transcript lands in the measurement instead of hiding behind the
// smaller one. The time budget is in probe units — the cost of one pair
// divided by the cost of one unit of the fixed calibration probe measured on
// the SAME runner in the SAME run — so the runner's speed cancels, and the
// budget is a constant in a machine-independent currency.
//
// WHY THE WINDOW IS APPENDS AT ~FIXED SIZE, NOT GROWTH FROM EMPTY
// --------------------------------------------------------------
// The common-prefix scan and the per-fold builder allocation are BOTH O(n)
// per change on dev today and are out of this slice's scope (#97 slices them
// separately) — measuring a growth drive from empty to N would therefore
// charge ~4× to the alloc leg regardless of the map fix and the gate would
// stay red on the CORRECT tree, the inverse of the #410 non-vacuity red: a
// bound that fails on right numbers (#939). Seeding the transcript unmeasured
// and then measuring a batch of appends isolates the per-CHANGE cost this
// slice touches.
//
// CONCURRENCY
// -----------
// [NotInParallel] — bare, not keyed: wall-clock elapsed is not thread-local,
// and the #1285 red (run 38129838108: time ×3.10 while alloc ×1.99 on a
// byte-identical tree) is the receipt that under TUnit's default parallel
// scheduling a neighbour burning cores decides the stopwatch. The allocation
// counter itself is GC.GetAllocatedBytesForCurrentThread, synchronous inside
// the window, so only the time leg actually needs the exclusivity — but both
// legs live in one test, so one attribute covers them.

using System.Collections.Immutable;
using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

[NotInParallel]
public sealed class ProjectHistoryScalingGates
{
    /// <summary>Seeded transcript lines — the "N" the gate doubles.</summary>
    /// <remarks>
    ///     512, not 100: the per-change cost is O(N) scan + O(N) builder
    ///     work, and a leg must dwarf its own bookkeeping so the measurement
    ///     is the code under test, not the harness dispatch.
    /// </remarks>
    private const int TranscriptLines = 512;

    /// <summary>Measured append+Project pairs per window, at ~constant transcript size.</summary>
    private const int AppendsPerWindow = 128;

    /// <summary>
    ///     The transcript the gate measures AT: twice the seeded size, because
    ///     that is the regime where a per-change cost that grows with the
    ///     transcript shows up.
    /// </summary>
    private const int MeasuredTranscriptLines = TranscriptLines * 2;

    /// <summary>Appends driven (and discarded) before the measured window.</summary>
    /// <remarks>
    ///     The first pass over ProjectHistory's append branch pays tiered-JIT
    ///     compilation of the loop and of everything it reaches. Like the
    ///     #410 warm-up, it would otherwise land on the first measured round
    ///     and inflate it.
    /// </remarks>
    private const int WarmupAppends = 8;

    /// <summary>
    ///     Allocation budget for ONE append+project pair, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    ///     The measured value on the current tree is 98 547 B per pair at 1024
    ///     transcript lines — the same number the pre-#1747 ratio gate measured
    ///     (12 318.3 KiB over 128 pairs, run 39123327931) and the same number
    ///     the #1747 gate measured (run 39372950798) — so the budget leaves
    ///     ~1.33x headroom.
    /// </summary>
    private const long MaxBytesPerPair = 128 * 1024;

    /// <summary>
    ///     Time budget for ONE append+project pair, in probe units (see
    ///     `PerfGate`). Machine-scaled and soft. Pinned from the same CI run:
    ///     297 probe units per pair against a 1200 budget.
    /// </summary>
    private const double MaxProbeUnitsPerPair = 1_200.0;

    [Test]
    [Timeout(300_000)]
    public async Task AppendOnly_CostPerChange_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Same guard rationale as StorePathScalingGates: the stopwatch and
            // the thread-local allocation counter belong to a measurement,
            // this project is not in ci.yml's `test-os` list anyway.
            return;
        }

        // Non-vacuity before the measurement: the script must actually be the
        // append-only path this slice touches. Asserted once here (as a
        // positive control) and inside DriveLineCost every round.
        await AssertAppendOnlyShape();

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: units => Task.FromResult(DriveLineCost(MeasuredTranscriptLines, units)),
            units: AppendsPerWindow,
            options: new PerfGateOptions
            {
                Label = "project-history-cost",
                MaxBytesPerUnit = MaxBytesPerPair,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerPair,
            });

        await result.AssertWithinBudgetsAsync("ui store append+project per change");
    }

    /// <summary>
    ///     Drives one window: seed <paramref name="lines" /> transcript lines
    ///     unmeasured, project once (cold — builds the first cache and its
    ///     map), warm the append branch, then measure a batch of appends.
    /// </summary>
    /// <remarks>
    ///     The transcript grows by only <see cref="AppendsPerWindow" /> lines
    ///     inside the measured window, so the window measures the per-CHANGE
    ///     cost at approximately-fixed size — see the file-header note on why a
    ///     growth-from-empty drive would measure neighbours outside this slice.
    /// </remarks>
    private static PerfGateLeg DriveLineCost(int lines, int appends)
    {
        var store = new UiStore();
        for (int i = 0; i < lines; i++)
        {
            // Distinct texts: the first-occurrence map's dedupe path is
            // exercised by the semantics test, and in this window every line
            // is unique like a real transcript's user/assistant turns.
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"hist-{i:000000}"));
        }

        var projector = new DefaultUiProjector();
        _ = projector.Project(store.State);
        _ = DriveWindow(store, projector, WarmupAppends, prefix: $"warm-{lines}");

        // Collect before the window: a collection inside it would be billed
        // to the code under test and land on one leg, not the other.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int appended = DriveWindow(store, projector, appends, prefix: $"tail-{lines}");
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The measurement must be of the script, not of an exited early: the
        // window appended exactly `appends` lines on top of the seed.
        // (The seed left the store at `lines` + WarmupAppends lines.)
        GC.KeepAlive(store.State);
        if (appended != appends)
        {
            throw new InvalidOperationException(
                $"Append-only script drifted: expected {appends} appends, drove {appended}.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    /// <summary>Append+Project pairs against a shared store. Returns the append count.</summary>
    private static int DriveWindow(UiStore store, DefaultUiProjector projector, int appends, string prefix)
    {
        for (int i = 0; i < appends; i++)
        {
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"{prefix}-{i:0000}"));
            _ = projector.Project(store.State);
        }

        return appends;
    }

    /// <summary>
    ///     The positive control for the drive above: an append-only script
    ///     over 2N lines must leave the store holding the expected line count
    ///     and the projector returning a transcript whose history rows carry
    ///     the reference first-equal-wins ids — so the gate's green says the
    ///     SCRIPT ran this slice, not some other path.
    /// </summary>
    /// <remarks>
    ///     Zero-allocation accounting is not the point here (the bookkeeping
    ///     allocations below would pollute a measurement window), which is
    ///     why the control runs outside the cost drive entirely — the same
    ///     two-drives split StorePathScalingGates documents.
    /// </remarks>
    private async Task AssertAppendOnlyShape()
    {
        var store = new UiStore();
        for (int i = 0; i < TranscriptLines; i++)
        {
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"hist-{i:000000}"));
        }

        var projector = new DefaultUiProjector();
        UiScreenModel screen = projector.Project(store.State);
        await Assert.That(store.State.Chat.Lines.Length).IsEqualTo(TranscriptLines);
        int projectedHistory = Math.Min(store.State.Chat.Lines.Length, screen.Transcript.RenderedLines.Count);
        await Assert.That(projectedHistory).IsEqualTo(TranscriptLines);

        await AssertIdsFollowFirstEqualWins(store.State, screen);

        _ = DriveWindow(store, projector, WarmupAppends, prefix: "control-warm");
        screen = projector.Project(store.State);
        await AssertIdsFollowFirstEqualWins(store.State, screen);

        await Assert.That(store.State.Chat.Lines.Length)
            .IsEqualTo(TranscriptLines + WarmupAppends)
            .Because(
                "the script is append-only AppendLine traffic: " + TranscriptLines + " seeded lines plus "
                + WarmupAppends + " appends must leave exactly that many Chat.Lines entries.");
    }

    // =====================================================================
    // 2. Semantics: first-equal-wins BlockIds through appends and rebuilds.
    // =====================================================================

    /// <summary>
    ///     The extension must be a BEHAVIOURAL no-op: every appended-history
    ///     BlockId equals the historical `Lines.IndexOf(line)` (first equal
    ///     line wins) — including appended DUPLICATES of prefix lines, whose
    ///     first occurrence sits before the appends the extension never
    ///     re-scanned, and the rebuild path after a HydrateSession rewrite,
    ///     where leftover map entries must not leak a stale index forward.
    /// </summary>
    [Test]
    public async Task BlockIds_KeepFirstEqualWins_ThroughAppends_AndRebuilds()
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();

        // alpha, beta, alpha-dup: the duplicate's id must point at index 0.
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "alpha"));
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.Assistant, "beta"));
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "alpha"));

        UiScreenModel screen = projector.Project(store.State);
        await AssertIdsFollowFirstEqualWins(store.State, screen);

        await Assert.That(screen.Transcript.RenderedLines[0].Id)
            .IsEqualTo("msg:0")
            .Because(
                "the first alpha is a user line without a ToolCallId, so its fallback id is the historical "
                + "role+index form with the FIRST-occurrence index: msg:0.");

        // Pure appends. The third append duplicates line 0 again — its
        // first-occurrence entry lives in the part of the map the extension
        // must carry over from the prefix, not re-derive.
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "appended-1"));
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "alpha"));
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.Tool, "call", "tc-1"));
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.ToolResult, "call-result", "tc-1"));

        screen = projector.Project(store.State);
        await AssertIdsFollowFirstEqualWins(store.State, screen);

        await Assert.That(screen.Transcript.RenderedLines[4].Id)
            .IsEqualTo("msg:0")
            .Because(
                "the second duplicated alpha sits at index 4, but its first occurrence is index 0 (before every "
                + "append this projection extended over): first-equal-wins must survive the incremental "
                + "extension.");
        await Assert.That(screen.Transcript.RenderedLines[5].Id)
            .IsEqualTo("tc-1")
            .Because(
                "a line with a ToolCallId takes the tool-call key, never the index fallback — the ToolCallKey "
                + "contract");
        await Assert.That(screen.Transcript.RenderedLines[6].Id)
            .IsEqualTo("tc-1")
            .Because(
                "the call and result lines of one invocation share the tool-call key, which is what joins the "
                + "transcript to the tool cards (#94).");

        // Rewrite: HydrateSession replaces Lines outright (the compaction
        // shape) — a changed prefix must force a full map rebuild, and the
        // ids after it must STILL be first-equal-wins over the NEW history.
        // A left-over entry from the old map ("alpha" -> 0) would return a
        // stale index here: in the rewritten history gamma holds index 0.
        ImmutableArray<ChatLine> rewritten = store.State.Chat.Lines.SetItem(0, new ChatLine(ChatRole.User, "gamma"));
        store.Dispatch(new ChatAppMsg.HydrateSession("model-x", "prov-x", "code", rewritten));

        screen = projector.Project(store.State);
        await AssertIdsFollowFirstEqualWins(store.State, screen);
        await Assert.That(screen.Transcript.RenderedLines[0].Id)
            .IsEqualTo("msg:0")
            .Because(
                "the rewritten line 0 (gamma) is its own first occurrence — a map extended from the previous "
                + "history would answer for the OLD line here, so this asserts the rebuild actually happened "
                + "and the stale entry was not reused forward.");

        // Appends after the rebuild extend the FRESH map.
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "gamma"));
        store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, "appended-after-rebuild"));
        screen = projector.Project(store.State);
        await AssertIdsFollowFirstEqualWins(store.State, screen);
        await Assert.That(screen.Transcript.RenderedLines[0].Id)
            .IsEqualTo("msg:0")
            .Because(
                "the appended duplicate of the rewritten line 0 must inherit the REBUILT map's first-occurrence "
                + "entry (index 0), not an index the append path re-derived or leaked from the discarded map.");
    }

    /// <summary>
    ///     Every history row's projected id equals a reference first-equal-
    ///     wins mapper over the CURRENT <c>Chat.Lines</c> — computed
    ///     independently of <see cref="DefaultUiProjector" />.
    /// </summary>
    private static async Task AssertIdsFollowFirstEqualWins(UiState state, UiScreenModel screen)
    {
        var lines = state.Chat.Lines;
        var first = new Dictionary<ChatLine, int>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            first.TryAdd(lines[i], i);
        }

        var rendered = screen.Transcript.RenderedLines;
        for (int i = 0; i < lines.Length; i++)
        {
            string expected = ToolCallKey.TranscriptBlockId(lines[i], first[lines[i]]);
            await Assert.That(rendered[i].Id)
                .IsEqualTo(expected)
                .Because(
                    "block ids must follow the historical `Lines.IndexOf` semantics (first equal line wins) "
                    + "under the incremental first-occurrence map — line " + i + " of "
                    + lines.Length + " projected " + rendered[i].Id + " against expected " + expected + ".");
        }
    }
}
