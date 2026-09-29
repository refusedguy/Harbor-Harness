// AllocationMeasurementGuardTests.cs — the #741 guard.
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
// satisfies every ceiling, so the gate was not merely imprecise — it was OFF,
// while rendering green. An absent guard is more honest than a gate that always
// passes.
//
// WHAT IS ASSERTED HERE, AND WHY IT IS NOT ONLY ABOUT TODAY
// --------------------------------------------------------
// The tripwires themselves now carry a lower bound, but a bound inside the test
// that would have caught it cannot be relied on to catch the NEXT one: each
// tripwire is written, reviewed and deleted on its own schedule, and the failure
// mode is silent by nature — a budget that quietly stops budgeting.
//
// So the invariant is asserted here, against the shared seam, once:
//
//   * a measurement across a thread hop is never negative (the #741 shape);
//   * a measurement of real work is never a silent ~0, because 0 is also what a
//     genuinely allocation-free path legitimately reports and the two are
//     indistinguishable at a call site;
//   * the synchronous path, which stays per-thread because it is exact there,
//     still reports real bytes and still survives a body that BLOCKS on another
//     thread (blocking is not a hop: this thread never stopped being this thread).
//
// NOT VACUOUS
// -----------
// `PerThreadWindow_AcrossAThreadHop_DoesGoNegative_IsTheWholeDefect` keeps the
// OLD methodology in the file, verbatim, and asserts that it does go negative.
// Without it the tests above could be passing because their own arithmetic is
// wrong. If a future runtime changed how per-thread allocation is accounted for,
// this control fails and says so, instead of leaving the others inert.
//
// DETERMINISM
// -----------
// The negative case is built, not hoped for: this thread allocates 4 MiB of
// ballast before the first sample and the second sample is taken on a thread
// whose counter started at zero, so the difference is negative by arithmetic and
// does not depend on where a scheduler resumed a continuation.
//
// [NotInParallel] keyless: the process-wide measurement bills every allocation in
// the process for the duration of its window, and TUnit runs classes in parallel
// by default.

using Harbor.TestKit;

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

    /// <summary>
    ///     Runs <paramref name="body" /> on a dedicated thread and completes only
    ///     once that thread is finished, so the awaiting continuation genuinely
    ///     resumes somewhere other than where it started.
    /// </summary>
    private static Task HopToADedicatedThreadAsync(Action body) =>
        Task.Factory.StartNew(
            body,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    [Test]
    public async Task ProcessMeasurement_AcrossAThreadHop_IsNeverNegative()
    {
        // The invariant #741 needed and did not have, asserted against the seam
        // every tripwire now shares. A process-wide delta is a difference of one
        // monotonic counter, so the thread the work lands on cannot make it go
        // backwards.
        long allocated = await AllocationProbe.MeasureProcessAsync(
            async () => await HopToADedicatedThreadAsync(() => { }));

        await Assert.That(allocated).IsGreaterThanOrEqualTo(0)
            .Because(
                "a measurement across a thread hop must never be negative. On the per-thread counter it " +
                "was -815859 B, and a ceiling-only tripwire accepts every negative, so the gate was off " +
                "while green (#741). If this fails, the seam has gone back to a per-thread counter");
    }

    [Test]
    public async Task ProcessMeasurement_OfRealWork_ReportsTheBytesItSaw()
    {
        // The other direction. A probe that always answered 0 would satisfy the
        // non-negativity claim above while measuring nothing, and 0 is exactly
        // what a real zero-allocation path also reports.
        long allocated = await AllocationProbe.MeasureProcessAsync(() =>
        {
            _ = new byte[256 * 1024];
            return Task.CompletedTask;
        });

        await Assert.That(allocated).IsGreaterThanOrEqualTo(256L * 1024L)
            .Because(
                "a window that allocated 256 KiB must report at least that; a ~0 report is the " +
                "silent-zero failure that a monotonic-counter guard alone cannot see");
    }

    [Test]
    public async Task ThreadMeasurement_OfRealWork_ReportsTheBytesItSaw()
    {
        // The cheap exact path — still per-thread, because a body with no await
        // cannot move threads — pinned to real work for the same reason.
        long allocated = AllocationProbe.MeasureThread(() =>
        {
            _ = new byte[128 * 1024];
        });

        await Assert.That(allocated).IsGreaterThanOrEqualTo(128L * 1024L)
            .Because("a synchronous body that allocated 128 KiB must report at least that");
    }

    [Test]
    public async Task ThreadMeasurement_OfABodyThatBlocks_IsStillExact()
    {
        // A blocking body is the nearest a synchronous Action comes to #741's
        // shape, and it is the case a reviewer will ask about: the work happens
        // on another thread, but THIS thread is what gets measured, and it never
        // stopped being this thread. Exact is the right answer — the foreign
        // thread's bytes were never in this counter and are not silently added.
        using var gate = new ManualResetEventSlim(false);
        Task<long> foreign = Task.Factory.StartNew(
            () =>
            {
                gate.Wait();
                return GC.GetAllocatedBytesForCurrentThread();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        long allocated = AllocationProbe.MeasureThread(() =>
        {
            _ = new byte[64 * 1024];
            gate.Set();
            foreign.GetAwaiter().GetResult();
        });

        await Assert.That(allocated).IsGreaterThanOrEqualTo(64L * 1024L)
            .Because(
                "blocking on another thread does not move THIS thread's counter, so the synchronous " +
                "measurement stays exact rather than becoming the #741 garbage");
    }

    [Test]
    public async Task PerThreadWindow_AcrossAThreadHop_DoesGoNegative_IsTheWholeDefect()
    {
        // The control, and the reason the tests above are worth having: the OLD
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
                "negative by arithmetic. Without this, the guards above could be passing because their " +
                "own arithmetic is wrong");
    }
}
