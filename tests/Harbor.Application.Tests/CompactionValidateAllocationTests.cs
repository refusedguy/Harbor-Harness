// CompactionValidateAllocationTests.cs — #799, the guard for the allocation half
// of the issue. RED BY CONSTRUCTION in the commit that adds it; the fix lands in
// the next one and this file is what makes that commit's claim checkable.
//
// WHAT WAS ACTUALLY FOUND (checked in the source, not taken on faith)
// ---------------------------------------------------------------------
// The issue claimed `CompactionConfig.Validate` allocates `new List<string>(3)`
// "on every call, including the one where all three values are valid". That holds
// exactly as written: the list is created ABOVE all three predicates, so the
// allocation happens on entry rather than on the way to a failure. `List<T>`'s
// capacity-taking constructor allocates the backing `string[3]` inside the
// constructor, so the all-valid call pays for two objects — the List and its
// array — and drops them having added nothing. It is NOT a lazily built list,
// and the capacity is set at construction, so neither reading rescues it.
//
// WHY THIS IS A GUARD AND NOT A COMMENT
// ------------------------------------
// A comment saying "don't allocate here" is not a gate: the next edit that adds a
// fourth rule, or lifts the accumulation into a helper, brings the list back and
// nothing says so. What is asserted is the one thing the fix must make true and
// then keep true — the answer that is almost always the answer, "fine", costs
// nothing on the heap. It is deliberately NOT a test for the aggregate error
// message being shorter, nor for the method being "fast": see
// `Validate_StillReportsEveryViolation` for what is being protected instead.
//
// THE MEASUREMENT, AND WHY IT CAN BE TRUSTED
// ------------------------------------------
// `AllocationProbe.MeasureThread` is the repository's own probe (#741) and is the
// right one here: the measured body never awaits, so the per-thread counter is
// exact and no `[NotInParallel]` serialisation is needed. That is the opposite of
// `AgentLoopAllocationTests`, whose body awaits and therefore has to use the
// process-wide counter.
//
// Two things could make `IsEqualTo(0)` pass for the wrong reason, and both are
// closed in this file rather than assumed away:
//
//   * the probe never ran, ran on the wrong thread, or its window was not the
//     call — closed by `TheProbe_SeesAnAllocation_ThatTheOldShapeMade`, which
//     drives the SAME measurement helper over a body that provably allocates a
//     `List<string>(3)` — the exact pre-fix shape — and requires a non-zero
//     answer. A guard that cannot see the defect it was written for is a guard
//     that is believed and catches nothing.
//   * a one-off JIT/tiering allocation landed inside the measured window — closed
//     by warming the body before measuring and keeping the MINIMUM of three
//     rounds, so one noisy round cannot decide the verdict. Min-of-3 is the
//     `AgentLoopAllocationTests` convention, for the same reason.
//
// The expected number is exactly 0 rather than merely small because
// `Result<CompactionConfig>` is a `readonly struct` in the pinned
// CSharpFunctionalExtensions 3.7.0 (docs/ROP-API-INVENTORY.md §2, verified
// against the v3.7.0 tag), so `Result.Success(this)` is a struct copy and costs
// nothing either.
//
// HONEST SCOPE OF THE CLAIM — READ THIS BEFORE QUOTING THE FIX
// -------------------------------------------------------------
// This is not a cold-start speedup and the guard does not pretend otherwise.
// `Validate` is reached from `HarborConfig.Validate` on the config-load path,
// i.e. roughly once per process, so the bytes at stake are two small objects
// once — far below the noise floor of anything this repository measures. What is
// pinned here is the SHAPE of the method (no accumulation object on the success
// path) and it stays pinned; that is a claim about form, and it is stated as one.
//
// STATUS: never compiled locally. Local dotnet is forbidden in this repository
// (concurrent agents, shared box), so CI is the first build of this file and its
// log is the first evidence about whether the C# compiles.

using CSharpFunctionalExtensions;
using Harbor.Application.Configuration;
using Harbor.TestKit;

namespace Harbor.Application.Tests;

/// <summary>
///     #799 — <c>CompactionConfig.Validate</c> must build its error accumulator
///     only when there is an error to accumulate.
/// </summary>
public sealed class CompactionValidateAllocationTests
{
    /// <summary>Calls made before the window opens, so JIT and tiering are not measured.</summary>
    private const int WarmupCalls = 64;

    /// <summary>Windows measured; the smallest is the verdict.</summary>
    private const int Rounds = 3;

    /// <summary>
    ///     The rule. The answer a well-formed config gets is "fine", and it costs
    ///     nothing to say so.
    /// </summary>
    [Test]
    public async Task Validate_OnAWellFormedConfig_AllocatesNothing()
    {
        // Hoisted on purpose, and the reason is worth stating because the obvious
        // one-liner `_ = CompactionConfig.Default.Validate()` is a trap:
        // `CompactionConfig.Default` is a PROPERTY returning a FRESH record per
        // access (the #195 immutability note on that member says so), so touching
        // it inside the window would measure the record's own allocation and
        // report a number this test could never explain.
        CompactionConfig config = CompactionConfig.Default;

        long allocated = MeasureWarmed(() =>
        {
            _ = config.Validate();
        });

        await Assert.That(allocated).IsEqualTo(0L).Because(
            "the three predicates are three integer comparisons and `Result<T>` is a readonly "
            + "struct, so a well-formed config can be validated without touching the heap. "
            + "Measured " + allocated + " B. Before #799 this method opened with "
            + "`new List<string>(3)`, which allocates the List AND its backing array on entry — "
            + "before any predicate has run — and then discarded both.");
    }

    /// <summary>
    ///     Non-vacuity: the probe must be able to SEE the allocation this file
    ///     exists for, measured the same way.
    /// </summary>
    /// <remarks>
    ///     `IsEqualTo(0)` is a "nothing happened" assertion, and nothing happened
    ///     is exactly what a broken probe also reports. So the same
    ///     <see cref="MeasureWarmed" /> helper is driven over a body that is the
    ///     pre-fix shape verbatim — allocate the list first, add nothing, because
    ///     the config is valid — and must come back non-zero. If this fails, the
    ///     measurement above is not evidence of anything and the fix's central
    ///     claim is unverified.
    /// </remarks>
    [Test]
    public async Task TheProbe_SeesAnAllocation_ThatTheOldShapeMade()
    {
        CompactionConfig config = CompactionConfig.Default;

        long allocated = MeasureWarmed(() => AccumulateLikeTheOldValidateDid(config));

        await Assert.That(allocated).IsGreaterThan(0L).Because(
            "this body is #799's `Validate` with its three messages removed, and it allocates a "
            + "List<string>(3) plus its array whatever the config says. A measurement helper that "
            + "reports 0 for THIS cannot be used to conclude anything about the real method, so "
            + "the zero above would be a broken probe rather than a fix.");
    }

    /// <summary>
    ///     What the fix must NOT buy: the aggregate message still reports all
    ///     three violations.
    /// </summary>
    /// <remarks>
    ///     #799's own body offers a guard-ladder variant that returns on the
    ///     FIRST bad field, which allocates nothing on either path and reads like
    ///     the four sibling <c>Validate</c> methods in the same file. It is a
    ///     behaviour change: a config with all three fields wrong would report one
    ///     of them, and <c>HarborConfig.Validate</c> hands the joined text
    ///     straight to the config store, which is how the user learns what is
    ///     wrong with their file. That trade is not silent here — this test goes
    ///     red if a fix takes it.
    /// </remarks>
    [Test]
    public async Task Validate_StillReportsEveryViolation()
    {
        Result<CompactionConfig> all = new CompactionConfig(0, 0, 0).Validate();

        await Assert.That(all.IsFailure).IsTrue()
            .Because("all three fields are zero, so there is something to report");

        await Assert.That(all.Error).Contains("compaction.reserveTokens must be > 0")
            .Because("the message aggregates every violation, so one bad config is one round trip "
                + "rather than three edits and three reloads");
        await Assert.That(all.Error).Contains("compaction.keepRecentTokens must be > 0")
            .Because("see above — all three, not just the first");
        await Assert.That(all.Error).Contains("compaction.tailTurns must be > 0")
            .Because("see above — all three, not just the first");

        // And one bad field alone still names only that field: the aggregate is
        // not padded with messages for fields that are fine.
        Result<CompactionConfig> one = new CompactionConfig(0, 20000, 2).Validate();

        await Assert.That(one.IsFailure).IsTrue();
        await Assert.That(one.Error).IsEqualTo("compaction.reserveTokens must be > 0")
            .Because("only one field is invalid, so only one message belongs in the failure");

        // The success path is still a success, with the config itself in hand.
        Result<CompactionConfig> good = CompactionConfig.Default.Validate();

        await Assert.That(good.IsSuccess).IsTrue();
        await Assert.That(good.Value).IsEqualTo(CompactionConfig.Default);
    }

    /// <summary>
    ///     Runs <paramref name="body" /> once per measured window and returns the
    ///     smallest number of bytes any window saw.
    /// </summary>
    /// <remarks>
    ///     The delegate is built by the caller, so its own allocation is outside
    ///     every window — which is what lets a zero be a real zero rather than an
    ///     artefact of measuring the plumbing. Min, not mean or last: a single
    ///     GC-adjacent wobble in one round must not be able to fail the gate, and
    ///     taking the minimum means such a round is discarded rather than averaged
    ///     into the verdict.
    /// </remarks>
    private static long MeasureWarmed(Action body)
    {
        for (int i = 0; i < WarmupCalls; i++)
        {
            body();
        }

        long best = long.MaxValue;

        for (int round = 0; round < Rounds; round++)
        {
            long allocated = AllocationProbe.MeasureThread(body);
            if (allocated < best)
            {
                best = allocated;
            }
        }

        return best;
    }

    /// <summary>
    ///     #799's pre-fix accumulator, reduced to its allocation: create the list,
    ///     then add nothing because the config is valid.
    /// </summary>
    /// <remarks>
    ///     Kept as a literal transcription rather than a summary of the old code,
    ///     because this is the positive control for the measurement and a
    ///     paraphrase is not evidence.
    /// </remarks>
    private static void AccumulateLikeTheOldValidateDid(CompactionConfig config)
    {
        var errors = new List<string>(3);
        if (config.ReserveTokens <= 0) errors.Add("compaction.reserveTokens must be > 0");
        if (config.KeepRecentTokens <= 0) errors.Add("compaction.keepRecentTokens must be > 0");
        if (config.TailTurns <= 0) errors.Add("compaction.tailTurns must be > 0");
        _ = errors.Count;
    }
}
