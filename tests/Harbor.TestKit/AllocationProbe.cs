// AllocationProbe.cs — the ONE way to measure allocations in a test (#741).
//
// WHY THIS FILE EXISTS
// --------------------
// `GC.GetAllocatedBytesForCurrentThread()` counts bytes allocated *by the current
// thread*. In async code that is not a smaller number, it is a wrong one: a
// continuation after `await` may resume on a different thread, whose counter
// started at zero. `before` is then read from one thread and `after` from
// another, and the difference is the gap between two counters that never met.
//
// On the AgentLoop turn path that gap was NEGATIVE — `text-only turn avg =
// -815859 B` — and the tripwire compared it with `best <= ceiling`, i.e. from
// ABOVE only. Every negative number satisfies every ceiling, so the gate was not
// merely imprecise, it was OFF, while rendering green. A measurement that cannot
// be negative is what makes an upper-bound comparison mean anything.
//
// ONE METHODOLOGY, NOT TWO
// ------------------------
// The repository had two incompatible conventions, each born of a local need:
// per-thread in AgentLoopAllocationTests, process-wide in
// JsonlUnboundedAllocationTests (after #661). A third would have appeared by
// Friday. Both are expressed here instead, and the choice between them is forced
// by the code under test rather than by taste:
//
//   * a window that may contain an `await` → MeasureProcessAsync, the
//     process-wide counter. Correct across thread hops; bills whatever else the
//     process allocated meanwhile, so the caller must be [NotInParallel].
//   * a window with no `await` at all       → MeasureThread, the per-thread
//     counter. Exact, and needs no serialisation.
//
// WHY AN EXPLICIT FAILURE RATHER THAN A QUIET ZERO
// ------------------------------------------------
// A measurement that could not be taken must not be reported as `0`, because `0`
// is also what a genuinely allocation-free path legitimately measures, and the
// two are indistinguishable at the call site. So the probe either returns a real
// number or throws — there is no in-between state to accidentally assert on, and
// no negative value for a budget to swallow. The guard tests in
// `Harbor.Application.Tests.AllocationMeasurementGuardTests` assert both halves
// of that contract, so a future change that reintroduces a silent zero fails
// there rather than quietly disarming a budget downstream.

namespace Harbor.TestKit;

/// <summary>
///     Measures how many bytes a test operation allocates, in a way that stays
///     correct when the operation spans an <c>await</c>.
/// </summary>
/// <remarks>
///     <para>
///         Two entry points; the choice is forced by the measured body:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="MeasureProcessAsync" /> — whenever the body can
///                 suspend. Safe across thread hops. The calling class must be
///                 marked keyless <c>[NotInParallel]</c>, because the process
///                 counter bills every allocation in the process for the duration
///                 of the window and TUnit runs classes in parallel by default.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="MeasureThread" /> — only for a body that provably
///                 cannot suspend. Exact, and needs no serialisation.
///             </description>
///         </item>
///     </list>
/// </remarks>
public static class AllocationProbe
{
    /// <summary>
    ///     Bytes allocated while <paramref name="body" /> ran, measured
    ///     process-wide so that an <c>await</c> inside it is harmless.
    /// </summary>
    /// <param name="body">The operation to measure. May await freely.</param>
    /// <returns>Bytes allocated process-wide during the window.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The counter went backwards, which no process-wide delta can do. The
    ///     measurement is void, and is reported as a failure rather than as a
    ///     number no budget could act on.
    /// </exception>
    public static async Task<long> MeasureProcessAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        await body().ConfigureAwait(false);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        if (allocated < 0)
        {
            throw new InvalidOperationException(
                $"[alloc-probe] the process-wide allocation counter went backwards by {-allocated} B. " +
                "A process-wide delta cannot be negative, so this measurement is void — refusing to " +
                "report it rather than handing a budget a number that would read as a cheap turn.");
        }

        return allocated;
    }

    /// <summary>
    ///     Measures a body that produces a value, and returns that value
    ///     alongside the bytes it allocated.
    /// </summary>
    /// <typeparam name="T">What the measured body produces.</typeparam>
    /// <param name="body">The operation to measure. May await freely.</param>
    /// <returns>
    ///     The body's result, and the bytes allocated process-wide while it ran.
    /// </returns>
    /// <remarks>
    ///     Exists so a measured window can return its own result instead of
    ///     forcing the caller to hoist a captured local out of the lambda — which
    ///     in practice means naming the result's type, and that type is often
    ///     <c>internal</c> to the assembly under test.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The process-wide counter went backwards. See
    ///     <see cref="MeasureProcessAsync(Func{Task})" />.
    /// </exception>
    public static async Task<(T Result, long Allocated)> MeasureProcessAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        T result = await body().ConfigureAwait(false);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        if (allocated < 0)
        {
            throw new InvalidOperationException(
                $"[alloc-probe] the process-wide allocation counter went backwards by {-allocated} B. " +
                "A process-wide delta cannot be negative, so this measurement is void — refusing to " +
                "report it rather than handing a budget a number that would read as a cheap turn.");
        }

        return (result, allocated);
    }

    /// <summary>
    ///     Bytes allocated while <paramref name="body" /> ran, measured on the
    ///     calling thread.
    /// </summary>
    /// <param name="body">
    ///     The operation to measure. Must not suspend: a continuation on another
    ///     thread would make the two samples come from different counters, and
    ///     the difference of two unrelated counters is arbitrary — including
    ///     negative.
    /// </param>
    /// <returns>Bytes allocated on the calling thread during the window.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The per-thread counter went backwards, which means the body suspended
    ///     after all. This is #741 caught at the only place that can still see it.
    /// </exception>
    public static long MeasureThread(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        long before = GC.GetAllocatedBytesForCurrentThread();
        body();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated < 0)
        {
            throw new InvalidOperationException(
                $"[alloc-probe] the per-thread allocation counter went backwards by {-allocated} B. " +
                "That means the measured body suspended and resumed on another thread, so the two " +
                "samples are not comparable. Use MeasureProcessAsync for a body that can await.");
        }

        return allocated;
    }
}
