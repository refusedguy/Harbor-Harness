// StorePathScalingGates.cs — #410: the store path's SCALING is a merge gate,
// and the gate is RELATIVE to a measurement taken in the same run.
//
// WHY THIS FILE IS ABOUT RATIOS AND NOT NUMBERS
// ---------------------------------------------
// #60 measured this path once and wrote the numbers down
// (docs/BENCHMARKS.md, `StreamingDeltaFrequencyBenchmark`):
//
//     1000 deltas -> 475 us / 1.03 MB
//     2000 deltas -> 1.09 ms / 2.46 MB
//
// which says "roughly linear" and nothing else. The finding lived in a
// benchmark no PR runs, so nothing failed when a later change made the store
// path 10x worse. This file is the missing merge gate, and it deliberately
// does NOT encode 475 us.
//
// A 475 us floor is not portable, and this repository already has the receipts
// for why (#460, commit c5eb5e28, in the same words):
//
//     "it moved between runs (3.1, 4.9, 4.5 MiB on the same commit), so the
//      gate was measuring the platform"
//
// An absolute millisecond budget depends on the runner's core count, on
// whatever else the shard is doing, and on neighbours outside the process.
// Measured across six green `test (platform)` runs of `dev` on ONE unchanged
// commit, `DebouncedPluginWatcherTests.QuickSaveBurst_CollapsesToSingleModified`
// took 2.167 s, 2.624 s, 2.938 s, 3.009 s, 3.088 s and 2.702 s — a 1.43x
// spread on a test whose own budget is 125 ms, and the run that reddened
// (#980, run 36851514483) read 178 ms for five 8-byte file writes against that
// 125 ms. So: an absolute wall-clock threshold is a WRONG METRIC, not a badly
// chosen number. No value fixes it — a tighter number flakes more, a looser one
// stops being a quarter of the debounce and stops testing anything. The gate
// therefore compares N against 2N WITHIN one run, where the machine's speed
// appears in both operands and cancels.
//
// WHICH OF THE THREE "RELATIVE TO" OPTIONS THIS IS, AND WHY
// --------------------------------------------------------
//   1. Relative to the previous run of the same job. Rejected: it needs a
//      history artifact, and the history is demonstrably incomplete — 26 of the
//      last 60 `dev` `ci` runs (43.3%, measured over the same window) ended
//      `cancelled` with zero jobs (#951). A hole in that series is
//      indistinguishable from a regression, so the gate would either skip or
//      invent a baseline.
//   2. Relative to the median of the last N green runs. Rejected for the same
//      missing state, plus it needs somewhere to KEEP the state — a new axis,
//      and an axis is a declaration plus wiring, not a free choice.
//   3. Relative to the machine, against a baseline measured in the SAME run.
//      This one. No cross-run state, so no new axis and no new artifact, and it
//      is the answer the repository already reached twice for the same reason:
//      the #460 header-rewrite gate (a baseline on the small session, then the
//      same measurement on a 24 MiB one, asserting the DIFFERENCE) and the #465
//      twin A/B. Those two commits are the precedent this file extends.
//
// WHAT IS ASSERTED, AND WHY EACH BOUND HAS THE SHAPE IT DOES
// --------------------------------------------------------
// Cost, as a RATIO against a 3x bound for a 2x input:
//
//     t(2N)    <= 3 * t(N)
//     alloc(2N) <= 3 * alloc(N)
//
// Shape, as COUNTS, because a count is a property of the algorithm and not of
// the machine — the same argument `StreamingFrequencyTests` and
// `StoreDispatchAllocTests` already make, and the reason those two are the
// load-bearing gates while their stopwatches are not:
//
//     FullProjections <= folds + 2      — the transcript is recomposed per
//                                          FOLD, never per delta.
//     TailRebuilds    <= flushes + 1   — the projector rebuilds the tail at
//                                          most once per reducer flush.
//     Materializations <= deltas / 10   — the reducer's string-copy work is
//                                          sublinear in the delta count.
//     MarkdownParses  == 0             — see `LineRestyles` for what is actually
//                                          counted and why it stands in.
//
// `TailRebuilds` and `Materializations` describe the same underlying event from
// two sides, and are deliberately counted from DIFFERENT observations so they
// can disagree: a flush materialises the reducer's synced buffer, and the
// projector then rebuilds its tail. `Materializations` reads the reducer's
// buffer reference; `TailRebuilds` reads the projector's transcript instance.
// A reducer that materialised per delta breaks the first bound and not the
// second. A projector that re-resolved the tail on deltas which did not flush
// breaks the second and not the first. Either bug is invisible to the other
// counter, which is why both bounds exist.
//
// THE SCRIPT: WHY TWO MESSAGES
// ----------------------------
// One streaming message is not enough to make the restyle counter mean
// anything. With a single message the transcript is EMPTY until the final fold,
// so there is never an already-projected line to compare against and
// `LineRestyles` would read 0 on any tree whatsoever — a check that cannot fail
// is the #901 shape. Two messages give the second fold a one-line history to
// reuse, so `ProjectHistory`'s common-prefix scan is actually exercised: drop
// the scan and line 0 comes back as a new instance and the counter fires.
//
// ISOLATION
// ---------
// Every drive builds its own `UiStore` and its own `DefaultUiProjector` and
// keeps every counter in a local, so there is no static state, no counter to
// reset, and no order dependence between the two measurements in a test. The
// cost script and the shape script run separately and deliberately: the shape
// counters need bookkeeping allocations (a screen reference per delta, a
// rendered-line snapshot per fold) that would otherwise land inside the very
// allocation window being measured and be billed to the code under test.
//
// NOT IN PARALLEL
// ---------------
// BARE `[NotInParallel]`, not the keyed form — and the difference is
// load-bearing for the stopwatch, which is the half of this gate a key cannot
// protect. `[NotInParallel("alloc-tripwire")]` excludes only the classes that
// share the key (`StoreDispatchAllocTests`, `StatusBarFitPerfTests`); every
// other test in this assembly — `ProjectorThreadSafetyTests` among them, four
// threads times five hundred projections with no constraint at all — still
// runs concurrently under TUnit's default parallel scheduling. The old comment
// here called that harmless because the ALLOCATION counter is
// `GC.GetAllocatedBytesForCurrentThread` and the windows are synchronous, so
// nothing could be scheduled onto this thread between the two reads. True of
// the counter, and the reason for the red it missed: wall-clock elapsed is NOT
// thread-local. A neighbour burning cores slows the window, and under server
// GC (`DOTNET_gcServer=1` in every CI job) its allocation triggers collections
// that PAUSE this thread mid-stopwatch. The failure that forced the rewrite
// printed exactly that split: run 38129838108 (PR #1285, commit b5db32e2)
// measured time x3.10 against the x3.00 limit while the very same run measured
// alloc x1.99 — textbook linear memory, inflated wall clock — on a store path
// byte-identical to the tree that had passed the same 536-test binary forty
// minutes earlier on `dev` (run 38128592312, commit 12ea0cff). Same code, same
// test, different scheduling: the delta was the neighbour, not the code under
// test. `StorePathSkipsMarkdownParseTests` documents the same rule for its
// counter — a keyed attribute only excludes tests that share that key, so
// exclusive execution is what makes the number a measurement — and the
// `perf-gate` job pins `Harbor.Tui.PerfTests` with `taskset -c 1` for the same
// reason. Exclusive execution here is the in-process form of that pinning.
//
// NON-VACUITY
// -----------
//   1. `TheRatioRuleAnswersTheDeclaredQuestion` — a positive control over a
//      FIXED table of synthetic (small, large, limit) triples whose verdicts
//      are stated as data, with the limit carried PER ROW so the table pins the
//      decision function and not the tuned constant. Five of the seven rows
//      must be REJECTED, including a zero baseline and a negative one — the
//      #591 shape, where a rule weakened into dividing by anything reports a
//      plausible pass on a tree it never examined.
//   2. Every shape counter is shown NON-ZERO before it is bounded, so a counter
//      that silently read zero fails instead of satisfying a `<=`.
//   3. The gate shipped RED on purpose: the first commit carried a tightened
//      limit (1.05x) that had to fail on a correct tree, and the follow-up
//      relaxed it to 3x with the reasoning. A gate never observed red is not a
//      gate; the run log is quoted in the commit that fixed it.
//   4. That red run also caught a bound of mine that was too TIGHT rather than
//      too loose: the tail-rebuild bound started as `flushes + 1`, and the
//      correct tree measured exactly 67 against a bound of 67. A gate that
//      passes by zero margin fails on the next runner for a reason that has
//      nothing to do with the code, which is the same class of defect as an
//      absolute millisecond and one step further from visible. The bound is
//      now derived — tail rebuilds are also caused by every `IsStreaming`
//      transition, of which this script produces `2 * Messages - 1` — and the
//      correct tree sits at 67 against 69.

using System.Diagnostics;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.TestKit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #410: relative scaling gates over the store path — cost as a ratio
///     against a measurement taken in the same run, shape as counts. The file
///     header is the argument for why no absolute millisecond appears here.
/// </summary>
[NotInParallel]
public sealed class StorePathScalingGates
{
    /// <summary>Streaming messages in the scripted traffic.</summary>
    /// <remarks>
    ///     Two, not one, and the reason is non-vacuity rather than realism: the
    ///     second fold is the only thing that gives
    ///     <see cref="LineRestyles" /> an already-projected line to compare
    ///     against. See the script note in the file header.
    /// </remarks>
    private const int Messages = 2;

    /// <summary>Scripted deltas in total, split evenly across the messages.</summary>
    private const int Deltas = Messages * 500;

    /// <summary>Chunk width: 24 chars, the shape a token stream produces.</summary>
    private const int ChunkChars = 24;

    /// <summary>
    ///     Growth allowed for a DOUBLED input, on cost.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Chosen from the shape of the claim, not from a measurement. The
    ///         question is "is this still linear", and linear means
    ///         <c>t(2N)/t(N) = 2</c>. So 3 leaves 50% of headroom over the ideal
    ///         for a tiered-JIT recompile landing inside one leg and for a
    ///         collection that hits the 2N leg and not the N leg — while an
    ///         O(N^2) regression lands at 4 and cannot hide underneath it.
    ///     </para>
    ///     <para>
    ///         2 would be the sharpest defensible bound and would also redden a
    ///         correct tree whenever one leg took a collection the other did
    ///         not: the #939 shape, a rule that fails on right numbers. 8 is
    ///         what #465 rejected for the mirror-image reason — it sits above
    ///         the quadratic shape the gate exists to catch.
    ///     </para>
    ///     <para>
    ///         The measure lives in the SHARED Harbor.TestKit
    ///         <see cref="TestScalingGate" /> (#1652; since #1889 this project
    ///         references the shared file instead of keeping a local copy —
    ///         the TestKit core no longer drags Harbor.Application into the
    ///         slice this project measures): GC-fenced legs scaled toward
    ///         ~100 ms of real work, medians over five rounds,
    ///         x1.05 noise band on the time branch only. The hand-rolled
    ///         best-of-5 loop stayed at a ~10 ms leg, which was exactly small
    ///         enough for a neighbour or a collection to decide the ratio.
    ///     </para>
    /// </remarks>
    private const double GrowthLimit = 3.0;

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>One drive's worth of observations.</summary>
    /// <param name="Dispatches">Messages dispatched to the store.</param>
    /// <param name="Projects">Calls made to <c>DefaultUiProjector.Project</c>.</param>
    /// <param name="FastPathHits">
    ///     Project calls that returned the previously returned screen instance
    ///     untouched — the memoised no-op path.
    /// </param>
    /// <param name="FullProjections">
    ///     Project calls that recomposed the transcript because
    ///     <c>Chat.Lines</c> had been replaced underneath them. One per fold.
    /// </param>
    /// <param name="TailRebuilds">
    ///     Project calls that returned a DIFFERENT transcript model than the
    ///     previous one. The projector's own observation, and deliberately not
    ///     the same signal as <see cref="Materializations" />: a projector that
    ///     re-resolved the tail on a delta which did not flush would show up
    ///     here and nowhere else.
    /// </param>
    /// <param name="Materializations">
    ///     Reducer flushes, read off the state: each concatenates the pending
    ///     chunks onto the synced prefix, which is the only per-delta
    ///     string-copy work on this path.
    /// </param>
    /// <param name="Folds">
    ///     <c>MessageEnd</c> events dispatched — counted from the SCRIPT, not
    ///     observed. It has to be independent of
    ///     <see cref="FullProjections" /> or the bound
    ///     <c>FullProjections &lt;= folds + 2</c> would compare a count with
    ///     itself and hold on any tree whatsoever.
    /// </param>
    /// <param name="PolicyFlushes">
    ///     What <see cref="StreamingSync.ShouldFlush" /> asks for when replayed
    ///     over the same chunk sizes, plus the unconditional end-of-message
    ///     drain. Derived independently of the reducer, so
    ///     <see cref="TailRebuilds" /> is compared against the policy's decision
    ///     rather than against itself.
    /// </param>
    /// <param name="LineRestyles">
    ///     Already-projected transcript lines that came back as a DIFFERENT
    ///     instance. The observable behind the <c>MarkdownParses == 0</c> gate.
    /// </param>
    /// <param name="Elapsed">Wall time of the measured window; zero for the shape drive.</param>
    /// <param name="Allocated">Bytes allocated inside the measured window; zero for the shape drive.</param>
    private sealed record Drive(
        int Dispatches,
        int Projects,
        int FastPathHits,
        int FullProjections,
        int TailRebuilds,
        int Materializations,
        int Folds,
        int PolicyFlushes,
        int LineRestyles,
        TimeSpan Elapsed,
        long Allocated);

    /// <summary>
    ///     Dispatches the scripted traffic makes: a start, the per-message delta
    ///     count and an end, for each of the <see cref="Messages" /> messages.
    /// </summary>
    private static int ExpectedDispatches(int deltas) => Messages * ((deltas / Messages) + 2);

    [Test]
    [Timeout(120_000)]
    public async Task Cost_GrowsSubLinearly_InDeltas()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Both operands come from GC.GetAllocatedBytesForCurrentThread and
            // from a stopwatch. This project is not in ci.yml's `test-os` list,
            // so in practice this never skips in CI; it is here because the
            // guard belongs to the measurement rather than to the runner, and
            // because the two sibling tripwires in this assembly carry it.
            return;
        }

        // The leg is a REPEAT of the same unit, per the helper's own contract
        // (#1652): the unit is one full Deltas-delta drive — self-contained
        // store + projector, exactly the shape the gate is about — and the
        // scaler repeats it until the leg reaches ~100 ms of real work. It
        // must NOT amplify N itself (Deltas*units deltas in one drive): that
        // changes the problem the ratio is about, and a drive of 46k deltas
        // folds/flushes differently than 10 drives of 1k — the x3.77 alloc
        // ratio the scaled-drive variant measured on CI was that changed
        // shape, not the code under test. Probes (the first JIT-paying pass
        // over ChatAppReducer.Update / DefaultUiProjector.Project) are
        // discarded by the helper, so the discarded-first-iteration argument
        // still holds verbatim.
        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(RepeatDriveLeg(Deltas, units)),
            measureLargeLeg: units => Task.FromResult(RepeatDriveLeg(Deltas * 2, units)),
            initialUnits: 1,
            options: new TestScalingGateOptions
            {
                Label = "store-path-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "time(2N)/time(N)",
            linearWhy:
                "Linear is 2.00 and an O(N^2) regression is 4.00, so this bound separates them without "
                + "depending on how fast this runner is — which an absolute millisecond would, and which is why "
                + "#410 does not use one.");
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> full repeats of the same
    ///     <paramref name="deltas" />-delta drive, elapsed time and allocation
    ///     summed over the repeats. Each repeat rebuilds its own
    ///     <see cref="UiStore" /> + <see cref="DefaultUiProjector" />, and
    ///     <see cref="DriveCost" /> fences GC before its own inner window, so
    ///     repeat-to-repeat carryover is fenced on both scales.
    /// </summary>
    private static TestScalingGateLeg RepeatDriveLeg(int deltas, int units)
    {
        TimeSpan total = TimeSpan.Zero;
        long allocated = 0;
        for (int u = 0; u < units; u++)
        {
            Drive drive = DriveCost(deltas);
            total += drive.Elapsed;
            allocated += drive.Allocated;
        }

        return new TestScalingGateLeg(total.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Shape_IsBoundedBy_Folds_And_Flushes_NotBy_Deltas()
    {
        // Counts, not milliseconds: a count is a property of the algorithm and
        // holds on any machine, which is what makes this the load-bearing gate
        // and the stopwatch above only the ratio.
        Drive drive = DriveShape(Deltas);

        // Non-vacuity first. A counter reading zero satisfies every `<=`
        // below, so each one is shown to have counted something real BEFORE it
        // is shown to be within bounds. This is the #901 shape — two checks
        // passing on zero subjects.
        await Assert.That(drive.Dispatches)
            .IsEqualTo(ExpectedDispatches(Deltas))
            .Because(
                "the script is " + Messages + " messages of (MessageStart, " + (Deltas / Messages)
                + " TextDeltas, MessageEnd), so the store saw " + ExpectedDispatches(Deltas)
                + " dispatches. A different number means the script is not the one these bounds were derived "
                + "from. Dispatches = " + drive.Dispatches + ".");
        await Assert.That(drive.Projects)
            .IsEqualTo(ExpectedDispatches(Deltas))
            .Because(
                "every dispatch is followed by exactly one Project call, so the projector ran "
                + ExpectedDispatches(Deltas) + " times. Projects = " + drive.Projects + ".");
        await Assert.That(drive.FastPathHits)
            .IsGreaterThan(0)
            .Because(
                "the memoised no-op path is the whole reason the store path scales, and a run in which it never "
                + "fired was not measuring this code. FastPathHits = " + drive.FastPathHits + ".");
        await Assert.That(drive.Folds)
            .IsEqualTo(Messages)
            .Because(
                "each MessageEnd folds its streaming message into the transcript, so the transcript grew "
                + Messages + " times. Folds = " + drive.Folds
                + " — a zero here would leave `FullProjections <= folds + 2` bounding nothing.");
        await Assert.That(drive.PolicyFlushes)
            .IsGreaterThan(0)
            .Because(
                "the replayed policy must have asked for at least one flush over " + Deltas + " "
                + ChunkChars + "-char chunks (" + (Deltas * ChunkChars)
                + " chars, well past the 256-char exact threshold), or the tail-rebuild bound below compares "
                + "against zero. PolicyFlushes = " + drive.PolicyFlushes + ", Materializations = "
                + drive.Materializations + ".");

        // The projector-not-per-delta claim. Chat.Lines is append-only in this
        // script and changes once per message, so the transcript is recomposed
        // once per fold. A projector that re-walked history on every delta
        // would report Deltas here and fail.
        await Assert.That(drive.FullProjections)
            .IsLessThanOrEqualTo(drive.Folds + 2)
            .Because(
                "fullProjections/folds = " + drive.FullProjections + "/" + drive.Folds + " against a limit of "
                + "folds + 2 = " + (drive.Folds + 2) + " at N = " + Deltas
                + ". The transcript is append-only in this script, so the projector should recompose it once per "
                + "fold and never per delta; " + (drive.Projects - drive.FullProjections) + " of " + drive.Projects
                + " projections reused the cached transcript.");

        // The projector-tracks-the-reducer claim, against an INDEPENDENT
        // baseline: PolicyFlushes is what the documented flush policy asks for
        // over the same chunk sizes, so a projector rebuilding the tail on
        // deltas which did not flush exceeds it rather than being compared
        // against itself.
        //
        // The slack term is DERIVED, not fitted. A tail rebuild is not only
        // caused by a flush: `ProjectTail` treats `IsStreaming` as part of the
        // tail's identity (`cache.IsStreaming == state.Chat.IsStreaming`), so
        // every streaming transition forces a rebuild that no flush explains.
        // The script has one MessageEnd per message plus one MessageStart
        // between each pair of them, so 2*Messages - 1 of them. Measured 67
        // against a bound of 69 — the first cut of this bound was
        // `flushes + 1`, which the run log shows the correct tree landing
        // exactly on, and a gate that passes by zero is not a gate.
        int streamingTransitions = (2 * Messages) - 1;
        await Assert.That(drive.TailRebuilds)
            .IsLessThanOrEqualTo(drive.PolicyFlushes + streamingTransitions)
            .Because(
                "tailRebuilds = " + drive.TailRebuilds + " against a limit of policyFlushes + streaming "
                + "transitions = " + drive.PolicyFlushes + " + " + streamingTransitions + " = "
                + (drive.PolicyFlushes + streamingTransitions) + " at N = " + Deltas
                + ". The projector rebuilds the streaming tail when the reducer replaced the synced buffer, and "
                + "also whenever IsStreaming flips, because IsStreaming is part of the tail's identity "
                + "(DefaultUiProjector.ProjectTail). The script produces " + streamingTransitions
                + " such flips. A projector re-resolving the tail on every delta would report " + drive.Projects
                + " here and fail.");

        // The reducer's copy work, sublinear in the delta count. Deltas/10 is a
        // shape bound, not a millisecond one: the flush policy caps the pending
        // window, so materialisations are bounded by (chars / lag), not by
        // chars. Measured on this script: 66 of 1000 and 78 of 2000.
        await Assert.That(drive.Materializations)
            .IsLessThanOrEqualTo(Deltas / 10)
            .Because(
                "materializations/deltas = " + ((double)drive.Materializations / Deltas).ToString("F4")
                + " against a limit of 0.10 at N = " + Deltas + ": " + drive.Materializations
                + " materialisations for " + Deltas + " deltas (" + (Deltas * ChunkChars)
                + " chars). Each one concatenates the pending chunks onto the synced prefix, so a per-delta flush "
                + "policy would report " + drive.Projects + " here and turn this path back into the O(N^2) string "
                + "concatenation the ChunkedBuffer exists to prevent.");

        // Markdown. See the LineRestyles note for the mapping.
        await Assert.That(drive.LineRestyles)
            .IsEqualTo(0)
            .Because(
                "restyled/deltas = " + ((double)drive.LineRestyles / Deltas).ToString("F4") + " at N = " + Deltas
                + ": " + drive.LineRestyles + " already-projected transcript lines came back as a new instance. "
                + "The second message's fold is the load-bearing step here — it is the only point in this script "
                + "where a projected line exists to be reused, so this count is 0 because "
                + "DefaultUiProjector.ProjectHistory reused its common prefix, and not because there was "
                + "nothing to reuse. A markdown render necessarily produces a new line instance, so a non-zero "
                + "count means something on this path started re-resolving text that was already projected.");

        // Reported, not only bounded: the fast-path share is the number a reader
        // wants when this test goes red, and it is the one figure here that is
        // both machine-independent and directly actionable.
        Console.WriteLine(
            $"store-path-shape: {drive.Dispatches} dispatches, {drive.Projects} projects "
            + $"({drive.FastPathHits} fast-path), {drive.FullProjections} full projections, "
            + $"{drive.TailRebuilds} tail rebuilds, {drive.Materializations} materializations, "
            + $"{drive.PolicyFlushes} policy flushes, {drive.Folds} folds, {drive.LineRestyles} restyles "
            + $"(fast-path share {(double)drive.FastPathHits / drive.Projects:F3})");
    }

    // =====================================================================
    // 2. Non-vacuity: the positive control.
    // =====================================================================

    /// <summary>
    ///     The declared verdicts, as data. A ratio rule is a claim about shapes,
    ///     so the claim is pinned against a table of shapes rather than against
    ///     whatever a runner happens to produce today.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The limit is carried PER ROW rather than read from
    ///         <see cref="GrowthLimit" />, on purpose: the table then pins the
    ///         decision FUNCTION, so retuning the constant cannot silently
    ///         rewrite what the control is asserting. Changing
    ///         <see cref="GrowthLimit" /> is a tuning change; changing a verdict
    ///         below is a change to what the gate means, and only one of those
    ///         should be able to happen quietly.
    ///     </para>
    ///     <para>
    ///         Row 1 is the #60 measurement, so the function is anchored to a
    ///         real run of this path. Rows 4 and 5 are the shapes the gate
    ///         exists to reject — including the pre-#594 <c>AppReducer</c>
    ///         behaviour that made the streaming path quadratic in the first
    ///         place. Rows 6 and 7 are the #591 shape: a rule weakened far
    ///         enough to divide by anything must not report a pass on a
    ///         baseline it never measured.
    ///     </para>
    /// </remarks>
    private static readonly (string Name, double Small, double Large, double Limit, bool Accept)[] DeclaredContract =
    [
        ("#60 measured: 475 us -> 1.09 ms", 475.0, 1090.0, 3.0, true),
        ("linear with headroom: 500 -> 1400", 500.0, 1400.0, 3.0, true),
        ("exactly at the bound: 500 -> 1500", 500.0, 1500.0, 3.0, true),
        ("quadratic: 500 -> 2000", 500.0, 2000.0, 3.0, false),
        ("pre-#594 AppReducer shape: 1720 -> 7000", 1720.0, 7000.0, 3.0, false),
        ("zero baseline — nothing was measured", 0.0, 0.0, 3.0, false),
        ("negative baseline — a broken measurement", -1.0, 10.0, 3.0, false),
    ];

    [Test]
    public async Task TheRatioRuleAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredContract
            .Where(row => TestScalingGate.WithinLimit(row.Small, row.Large, row.Limit) != row.Accept)
            .Select(row =>
                row.Name + " (expected " + (row.Accept ? "accepted" : "rejected") + ", rule says "
                + (TestScalingGate.WithinLimit(row.Small, row.Large, row.Limit) ? "accepted" : "rejected") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the ratio gate is only meaningful while the rule still separates linear from quadratic and still "
                + "refuses to divide by a baseline it did not measure. If a row disagrees, the rule changed and a "
                + "green cost gate no longer means what it says. The rejected rows are the point: a rule that "
                + "accepted a 4x doubling, or that read a zero or negative baseline as a pass, would be the #591 "
                + "defect — a matcher weakened into returning a plausible verdict on a tree it had not examined. "
                + "Mismatches: " + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    // =====================================================================
    // helpers
    // =====================================================================

    /// <summary>
    ///     Drives the cost script: time and allocations only.
    /// </summary>
    /// <remarks>
    ///     Deliberately counter-free. The shape counters need bookkeeping — a
    ///     screen reference per dispatch, a rendered-line snapshot per fold —
    ///     that would land inside the allocation window and be billed to the
    ///     code under test, so the two questions are answered by two drives
    ///     rather than by one drive answering both.
    /// </remarks>
    private static Drive DriveCost(int deltas)
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();
        var partial = AssistantMessage.Empty("s", "m");
        string chunk = new('x', ChunkChars);
        int perMessage = deltas / Messages;

        // Collect first: a collection inside the window would be billed to the
        // measured code, and it would land on one leg and not the other.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();

        int dispatches = 0;
        int projects = 0;
        for (int m = 0; m < Messages; m++)
        {
            store.Dispatch(new ChatAppMsg.Agent(new MessageStartEvent(partial)));
            dispatches++;
            _ = projector.Project(store.State);
            projects++;

            for (int i = 0; i < perMessage; i++)
            {
                store.Dispatch(new ChatAppMsg.Agent(
                    new MessageUpdateEvent(new TextDeltaEvent("m", chunk), partial)));
                dispatches++;
                _ = projector.Project(store.State);
                projects++;
            }

            store.Dispatch(new ChatAppMsg.Agent(new MessageEndEvent(partial)));
            dispatches++;
            _ = projector.Project(store.State);
            projects++;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Nothing above may be optimised away: the final state is the product of
        // every dispatch, and dropping it would let the JIT elide work the gate
        // is measuring.
        GC.KeepAlive(store.State);

        return new Drive(
            Dispatches: dispatches,
            Projects: projects,
            FastPathHits: 0,
            FullProjections: 0,
            TailRebuilds: 0,
            Materializations: 0,
            Folds: 0,
            PolicyFlushes: 0,
            LineRestyles: 0,
            Elapsed: TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency),
            Allocated: allocated);
    }

    /// <summary>
    ///     Drives the same script for the shape counters, with no stopwatch and
    ///     no allocation accounting.
    /// </summary>
    private static Drive DriveShape(int deltas)
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();
        var partial = AssistantMessage.Empty("s", "m");
        string chunk = new('x', ChunkChars);
        int perMessage = deltas / Messages;

        int dispatches = 0;
        int projects = 0;
        int fastPath = 0;
        int fullProjections = 0;
        int tailRebuilds = 0;
        int materializations = 0;
        int folds = 0;
        int lineRestyles = 0;

        // The documented flush policy, replayed independently over the same
        // chunk sizes. This mirrors ChatAppReducer.WithTextDelta exactly: append
        // to pending, then ask ShouldFlush whether to materialise. Replayed per
        // message because OnMessageStart resets both lengths.
        int policySynced = 0;
        int policyPending = 0;
        int policyFlushes = 0;

        UiScreenModel? prevScreen = null;
        var prevLines = store.State.Chat.Lines;
        string? prevBuffer = store.State.Chat.Active.TextBuffer;
        UiTranscriptModel? prevTranscript = null;
        var prevHistory = Array.Empty<UiRenderedLine>();

        void Observe()
        {
            var state = store.State;
            var screen = projector.Project(state);
            projects++;

            if (prevScreen is not null && ReferenceEquals(screen, prevScreen))
            {
                fastPath++;
            }

            // ImmutableArray.Equals is backing-array reference equality, so this
            // reads "the transcript was replaced", not "the transcript differs".
            if (!state.Chat.Lines.Equals(prevLines))
            {
                fullProjections++;
                prevLines = state.Chat.Lines;
            }

            // REDUCER's observation. Reference equality suffices for the synced
            // buffers: they are immutable strings replaced wholesale on a
            // flush, so a changed reference IS changed content. The
            // `IsStreaming` and non-empty conditions exclude the two resets —
            // OnMessageStart and OnMessageEnd both swap in
            // ActiveMessage.Empty — which replace the buffer without
            // materialising anything. A flush, by contrast, always happens
            // mid-stream and always leaves a non-empty prefix.
            if (state.Chat.IsStreaming
                && !string.IsNullOrEmpty(state.Chat.Active.TextBuffer)
                && !ReferenceEquals(state.Chat.Active.TextBuffer, prevBuffer))
            {
                materializations++;
            }

            prevBuffer = state.Chat.Active.TextBuffer;

            // PROJECTOR's observation, and a different signal from the one
            // above: the transcript model is a new instance exactly when the
            // history or the tail was rebuilt. Keeping the two counters on
            // separate signals is the point — a projector that re-resolved the
            // tail on deltas which did not flush would move this one and not
            // the other, which is why both bounds are worth having.
            if (prevTranscript is not null && !ReferenceEquals(screen.Transcript, prevTranscript))
            {
                tailRebuilds++;
            }

            prevTranscript = screen.Transcript;

            // Already-projected transcript lines that came back as a DIFFERENT
            // instance. The transcript is rendered history-then-tail and
            // ProjectHistory reuses the unchanged prefix, so a correct tree
            // replaces nothing here. This is the observable behind the
            // `MarkdownParses == 0` gate: the store path styles whole lines
            // with no parser behind it (DefaultUiProjector.ResolveSpans), and
            // any parse — markdown included — necessarily allocates a new line
            // instance, so a re-projected line is the shape a parser on this
            // path would take.
            var rendered = screen.Transcript.RenderedLines;
            int historyLength = state.Chat.Lines.Length;
            int comparable = Math.Min(prevHistory.Length, historyLength);
            for (int i = 0; i < comparable; i++)
            {
                if (!ReferenceEquals(prevHistory[i], rendered[i]))
                {
                    lineRestyles++;
                }
            }

            if (historyLength != prevHistory.Length)
            {
                var grown = new UiRenderedLine[historyLength];
                for (int i = 0; i < historyLength; i++)
                {
                    grown[i] = rendered[i];
                }

                prevHistory = grown;
            }

            prevScreen = screen;
        }

        for (int m = 0; m < Messages; m++)
        {
            store.Dispatch(new ChatAppMsg.Agent(new MessageStartEvent(partial)));
            dispatches++;
            Observe();

            policySynced = 0;
            policyPending = 0;

            for (int i = 0; i < perMessage; i++)
            {
                store.Dispatch(new ChatAppMsg.Agent(
                    new MessageUpdateEvent(new TextDeltaEvent("m", chunk), partial)));
                dispatches++;

                policyPending += chunk.Length;
                if (StreamingSync.ShouldFlush(policySynced, policyPending))
                {
                    policyFlushes++;
                    policySynced += policyPending;
                    policyPending = 0;
                }

                Observe();
            }

            store.Dispatch(new ChatAppMsg.Agent(new MessageEndEvent(partial)));
            dispatches++;
            folds++;

            // FlushPending materialises the remainder unconditionally, which is
            // the "+ 1" the tail-rebuild bound allows. It is counted on the
            // policy side rather than the observed side because the observed
            // counter deliberately ignores it (IsStreaming is already false by
            // the time Observe runs).
            if (policyPending > 0)
            {
                policyFlushes++;
            }

            Observe();
        }

        return new Drive(
            Dispatches: dispatches,
            Projects: projects,
            FastPathHits: fastPath,
            FullProjections: fullProjections,
            TailRebuilds: tailRebuilds,
            Materializations: materializations,
            Folds: folds,
            PolicyFlushes: policyFlushes,
            LineRestyles: lineRestyles,
            Elapsed: TimeSpan.Zero,
            Allocated: 0);
    }
}
