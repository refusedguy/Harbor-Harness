// AllocationMeasurementGuardTests.cs — the #741 guard, RED STATE.
//
// THE BUG
// -------
// `GC.GetAllocatedBytesForCurrentThread()` counts bytes allocated BY THE CURRENT
// THREAD. A measurement window that spans an `await` is not a measurement of one
// thing: `before` is read on one thread and `after` on whatever thread the
// continuation resumed on, whose counter started near zero. The delta is the gap
// between two counters that never met, and on the AgentLoop turn path it was
// NEGATIVE — `text-only turn avg = -815859 B`.
//
// It did not fail, because the tripwire compared from ABOVE only:
// `Assert.That(best).IsLessThanOrEqualTo(ceiling)`. Every negative number
// satisfies every ceiling, so the gate was not imprecise — it was OFF, while
// rendering green. An absent guard is more honest than a gate that always passes.
//
// THIS IS THE RED STATE
// ---------------------
// The test below asserts the invariant the tripwire needed and never had: a
// measurement is never negative. It measures the way the repository measures
// today, so on this commit it FAILS — deterministically, not by luck. The
// ballast below is what makes it deterministic: this thread allocates 4 MiB
// before the first sample, the second sample is taken on a thread whose counter
// started at zero, so the difference is negative by arithmetic and cannot depend
// on where a scheduler happened to resume a continuation.
//
// The fix (the next commit) introduces one shared measurement seam and moves
// every allocation tripwire onto it. This file then asserts the same invariant
// against that seam, so a return to the old methodology turns it red again.
//
// NOT VACUOUS
// -----------
// `PerThreadWindow_AcrossAThreadHop_DoesGoNegative_IsTheWholeDefect` asserts the
// opposite statement — that the OLD methodology really does produce a negative —
// so the guard above cannot be passing because its own arithmetic is wrong. If a
// future runtime changed how per-thread allocation is accounted for, this test
// would fail and say so, instead of leaving the guard above inert.
//
// [NotInParallel] keyless: a measurement of a thread hop is only meaningful with
// nothing else on the process, and TUnit runs classes in parallel by default.

namespace Harbor.Application.Tests;

[NotInParallel]
public class AllocationMeasurementGuardTests
{
    /// <summary>Big enough that no freshly started thread's counter can reach it.</summary>
    private const int BallastBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Reads the per-thread allocation counter on a brand-new thread. A
    ///     thread's counter starts at zero, so this is the second sample of a
    ///     window that spanned a hop — the same relationship an <c>await</c>
    ///     produces whenever the continuation resumes elsewhere.
    /// </summary>
    private static Task<long> SampleCounterOnAFreshThreadAsync() =>
        Task.Factory.StartNew(
            () => GC.GetAllocatedBytesForCurrentThread(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    [Test]
    public async Task Measurement_AcrossAThreadHop_IsNeverNegative()
    {
        // The invariant #741 needed and did not have. Measured the way this
        // repository measures today, so it fails on this commit — by arithmetic,
        // not by timing.
        _ = new byte[BallastBytes];
        long before = GC.GetAllocatedBytesForCurrentThread();
        long after = await SampleCounterOnAFreshThreadAsync();
        long measured = after - before;

        await Assert.That(measured).IsGreaterThanOrEqualTo(0)
            .Because(
                "an allocation measurement is a difference of two readings of the SAME counter. Across a " +
                "thread hop the readings come from different counters and the difference is meaningless — " +
                "here it is negative, and a ceiling-only tripwire accepts every negative, so the gate is " +
                "off while green");
    }

    [Test]
    public async Task PerThreadWindow_AcrossAThreadHop_DoesGoNegative_IsTheWholeDefect()
    {
        // The control, and the reason the guard above is worth having: the old
        // methodology really does go negative, deterministically. This thread
        // allocated 4 MiB of ballast; the other thread started at zero.
        _ = new byte[BallastBytes];
        long before = GC.GetAllocatedBytesForCurrentThread();
        long after = await SampleCounterOnAFreshThreadAsync();
        long perThreadDelta = after - before;

        await Assert.That(perThreadDelta).IsLessThan(0)
            .Because(
                "this is the #741 number, kept as a positive control. The 'before' thread allocated 4 MiB " +
                "and the 'after' thread started at zero, so a per-thread delta across that hop is " +
                "negative by arithmetic. Without this, the guard above could be passing because its own " +
                "arithmetic is wrong");
    }
}
