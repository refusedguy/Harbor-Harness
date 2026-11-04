// AllocationCensus.cs — the per-test ALLOCATION census of the test tree.
//
// WHAT THIS FILE IS
// -----------------
// A measurement, not a gate. It answers one question the wall census cannot:
// "which tests allocate the most bytes".
//
// The wall census (tools/gates/test-census) reads the TRX and knows how long
// every test took. It cannot see MEMORY: a test that allocates 12 GiB and a
// test that allocates 12 KiB can both take 3 ms, and the difference between
// them is the difference between a shard that needs 8 GB of RSS and a shard
// that needs 400 MB. The one measured example the repository has is the
// architecture suite's comment stripper: 12 465 MiB allocated on one pass over
// the tree, of which 12 386 MiB came from ONE block comment in ONE file.
//
// HOW IT WORKS
// ------------
// TUnit's `[BeforeEvery(HookType.Test)]` and `[AfterEvery(HookType.Test)]` are
// GLOBAL hooks: they run around EVERY test in the assembly, whatever class
// declares them. That is the mechanism this file uses — there is no per-test
// opt-in, so a test that nobody instrumented is still measured.
//
// The counters are the two the BCL offers:
//
//   * `GC.GetAllocatedBytesForCurrentThread()` — EXACT, but only for a window
//     that stayed on one thread. An async test resumes on a thread-pool thread,
//     so the "after" sample can come from a different counter than the "before"
//     sample, and the difference of two unrelated counters is arbitrary — it
//     can even be negative. The probe therefore compares the thread ids and
//     reports the thread number ONLY when they match.
//   * `GC.GetTotalAllocatedBytes(precise: true)` — the process-wide counter.
//     Correct across thread hops, but it bills everything the process
//     allocated during the window, so under a parallel runner it is an UPPER
//     BOUND: it includes the tests running beside this one.
//
// The reported number is the thread number when the window stayed on one
// thread, and the process number when it did not. `scope=` says which one it
// is, and `process=` always carries the process number, so a reader can see the
// gap between the two.
//
// WHY IT IS OFF BY DEFAULT
// ------------------------
// The census writes one line per test into the test's own output block, and
// those lines land in the TRX. That is 8 000+ lines per shard, so the census
// is opt-in: `HARBOR_ALLOC_CENSUS=1` turns it on, and without that variable
// this file does nothing at all — no counters read, no line written, no cost.
//
// WHY IT NEVER FAILS A TEST
// --------------------------
// A measurement that can turn a green test red is a measurement nobody can
// trust. The write is wrapped: if the output sink refuses the line, the line
// is dropped and the test keeps its verdict.
//
// THE LINE
// --------
//     [alloc-bytes] bytes=123456 process=234567 scope=thread
//
// `tools/gates/test-census` reads it out of the TRX and renders the ranking.
//
// The marker is `[alloc-bytes]` and NOT `[alloc-census]`: the static census
// of the test tree (#2190) already prints `[alloc-census]` lines into the
// architecture suite's TRX, and two instruments that share a marker are two
// instruments whose lines a reader cannot tell apart.
//
// WHY IT IS A SEPARATE FILE, COMPILED INTO EVERY TEST PROJECT
// -----------------------------------------------------------
// The hook has to live in the assembly whose tests it measures: TUnit's
// generator discovers hooks in the compilation it generates for. A helper
// ASSEMBLY would not be discovered, so the file is linked into every test
// project by Directory.Build.targets (the condition is the same
// `IsTestProject` condition that injects TUnit itself). It is `internal`, so
// no test assembly's public surface grows and two assemblies that both carry
// it never collide.

using System.Globalization;

using TUnit.Core;

namespace Harbor.TestCensus;

/// <summary>
///     Measures how many bytes every test in this assembly allocates, and
///     writes the number into the test's own output block so the TRX of a CI
///     run carries the allocation census next to the wall census.
/// </summary>
/// <remarks>
///     <para>
///         The class is <c>internal</c> on purpose: it is a measurement
///         instrument that every test project compiles, and a public type
///         would be a public type in every one of them. TUnit's generator
///         discovers the hooks in the compilation it generates for, so the
///         methods are <c>public</c> while the class that holds them is not.
///     </para>
/// </remarks>
internal static class AllocationCensus
{
    /// <summary>
    ///     The environment variable that turns the census on. The census is off
    ///     unless this variable is set to <c>1</c>.
    /// </summary>
    internal const string EnableVariable = "HARBOR_ALLOC_CENSUS";

    /// <summary>
    ///     The marker the census tool looks for in a test's output block.
    /// </summary>
    internal const string LinePrefix = "[alloc-bytes]";

    /// <summary>
    ///     Whether the census runs at all. Read once, because an environment
    ///     lookup per test would cost more than the measurement it guards.
    /// </summary>
    private static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable(EnableVariable),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    ///     The snapshot taken before a test, on the thread the test starts on.
    /// </summary>
    /// <remarks>
    ///     It is <c>[ThreadStatic]</c> because the "after" sample has to come
    ///     from the same counter as the "before" sample — and the probe CHECKS
    ///     that it did, rather than assuming it. A thread that ran another
    ///     test's setup in between would otherwise report that other test's
    ///     allocation as its own.
    /// </remarks>
    [ThreadStatic]
    private static Snapshot _before;

    /// <summary>
    ///     One measurement window: where it started, and what the two counters
    ///     said at that moment.
    /// </summary>
    /// <param name="ThreadId">The managed thread the window opened on.</param>
    /// <param name="TestId">The test the window belongs to.</param>
    /// <param name="ThreadBytes">The per-thread counter at the start.</param>
    /// <param name="ProcessBytes">The process-wide counter at the start.</param>
    private readonly record struct Snapshot(
        int ThreadId,
        string TestId,
        long ThreadBytes,
        long ProcessBytes);

    /// <summary>
    ///     Opens the measurement window for a test.
    /// </summary>
    /// <param name="context">The test's context.</param>
    [BeforeEvery(HookType.Test)]
    public static void BeforeTest(TestContext context)
    {
        if (!Enabled)
        {
            return;
        }

        _before = new Snapshot(
            Environment.CurrentManagedThreadId,
            context.Id,
            GC.GetAllocatedBytesForCurrentThread(),
            GC.GetTotalAllocatedBytes(precise: true));
    }

    /// <summary>
    ///     Closes the measurement window for a test and writes the number into
    ///     the test's own output block.
    /// </summary>
    /// <param name="context">The test's context.</param>
    [AfterEvery(HookType.Test)]
    public static void AfterTest(TestContext context)
    {
        if (!Enabled)
        {
            return;
        }

        Snapshot before = _before;
        _before = default;

        long threadBytes = GC.GetAllocatedBytesForCurrentThread() - before.ThreadBytes;
        long processBytes = GC.GetTotalAllocatedBytes(precise: true) - before.ProcessBytes;

        // The thread number is only meaningful when BOTH samples came from the
        // same counter: the same thread, and the same test's window. A window
        // that hopped threads has no exact number at all, and the process
        // counter is the honest answer — an upper bound, labelled as such.
        bool sameThread = before.ThreadId == Environment.CurrentManagedThreadId;
        bool sameTest = string.Equals(before.TestId, context.Id, StringComparison.Ordinal);
        bool exact = sameThread && sameTest && threadBytes >= 0;

        long bytes = exact ? threadBytes : Math.Max(processBytes, 0);
        string scope = exact ? "thread" : "process";

        try
        {
            context.Output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{LinePrefix} bytes={bytes} process={processBytes} scope={scope}"));
        }
        catch (Exception ex)
        {
            // A measurement must never turn a green test red. The line is the
            // measurement; a sink that refuses it costs a row in the census,
            // not a verdict on the test.
            _ = ex;
        }
    }
}
