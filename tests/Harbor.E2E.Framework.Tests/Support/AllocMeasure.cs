// AllocMeasure.cs — the one per-thread allocation meter for the parity suites.
//
// Both parity suites average a synchronous body over their own iteration
// count. The meter used to be spelled out in each file, which made the two
// copies identical method bodies; it lives here once instead.

namespace Harbor.E2E.Framework.Tests;

internal static class AllocMeasure
{
    /// <summary>
    ///     Bytes one call of <paramref name="body" /> allocates, averaged over
    ///     <paramref name="iterations" /> calls on the calling thread.
    /// </summary>
    internal static long Measure(Action body, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            body();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return allocated / iterations;
    }
}
