using System.Diagnostics;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// #487 measurement tripwires for <see cref="StatusBarLayout.Fit"/> and for the
/// width cache underneath it.
///
/// <para><b>One deterministic gate, two stopwatches.</b> The load-bearing claim
/// — a Fit call measures each segment once, whatever the outcome — is a
/// property of the algorithm, so it is asserted through
/// <see cref="UnicodeWidth.BeginWidthLookupTracking"/> and holds on any
/// machine. A wall-clock assertion could only fail on a machine slow enough to
/// notice, which is what #465 got rewritten for. The stopwatches stay because
/// the issue was about a per-frame hot path and the lock was a cross-thread
/// property, and they are shaped like the rest of the repo's tripwires:
/// Linux-only (GC and scheduler accounting move several-fold across runtimes),
/// min-of-3 rounds, serialized against the other <c>alloc-tripwire</c> classes
/// so a neighbour cannot take the machine mid-measurement, and a band wide
/// enough to survive a shared runner while still failing the shape it was
/// written to catch.</para>
/// </summary>
[NotInParallel("alloc-tripwire")]
public class StatusBarFitPerfTests
{
    private const int Rounds = 3;
    private const int TimeIterations = 20_000;
    private const int CacheRunsPerThread = 100_000;
    private const int SpinIterations = 4_000_000;
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Model + mode hint (fixed), then the flexible run.</summary>
    private static StatusSeg[] StandardRow(int segments)
    {
        var row = new StatusSeg[segments];
        row[0] = new StatusSeg("kilocode/hilo3", StatusAccent.Accent, FixedPriority: true);
        row[1] = new StatusSeg("⏸ awaiting approval", StatusAccent.Warning, FixedPriority: true);
        for (int i = 2; i < segments; i++)
        {
            row[i] = new StatusSeg($"seg-{i} 12k↑ 4.5k", StatusAccent.Dim, FixedPriority: false);
        }

        return row;
    }

    [Test]
    public async Task Fit_MeasuresEachSegmentOnce_NoMatterHowManyAreDropped()
    {
        // The pre-#487 shape re-summed the row inside the shrink loop, so packing
        // this 24-segment row down to its fixed pair issued 323 width lookups
        // where the row has 24 segments — every one of them a process-global
        // monitor acquisition. Counting them is machine-independent: the number
        // is a property of the algorithm, not of the hardware.
        const int segments = 24;
        StatusSeg[] row = StandardRow(segments);
        StatusSeg[] warm = row.ToArray();
        _ = StatusBarLayout.Fit(warm, 600); // prime the width cache; drops nothing

        long before = UnicodeWidth.BeginWidthLookupTracking();
        int kept = StatusBarLayout.Fit(row, 40);
        long lookups = UnicodeWidth.EndWidthLookupTracking() - before;

        Console.WriteLine(
            $"#487 fit lookups: {segments} segments -> {kept} kept = {lookups} width lookups "
            + "(pre-#487: 323, each under a process-global monitor)");

        await Assert.That(kept).IsLessThan(segments)
            .Because("the width must actually force drops, or this gate proves nothing");
        await Assert.That(lookups).IsEqualTo((long)segments)
            .Because("Fit measures every segment once up front and carries the total; the old loop re-summed per victim");
    }

    [Test]
    public async Task Fit_MeasuresEachSegmentOnce_AtEveryWidth()
    {
        // Same invariant across the whole width sweep, including the narrow
        // widths where pass 2 character-cuts run. Truncation reports the width
        // it produced, so even the cut path must not re-measure.
        const int segments = 12;
        var problems = new List<string>();
        foreach (int width in new[] { 0, 1, 2, 5, 9, 13, 21, 34, 55, 89, 144, 400 })
        {
            StatusSeg[] row = StandardRow(segments);
            long before = UnicodeWidth.BeginWidthLookupTracking();
            _ = StatusBarLayout.Fit(row, width);
            long lookups = UnicodeWidth.EndWidthLookupTracking() - before;
            if (lookups != segments)
            {
                problems.Add($"width {width}: {lookups} lookups for {segments} segments");
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because("the width of the row is a post-measurement decision; it must not change how much measuring costs");
    }

    [Test]
    public async Task Fit_Cost_GrowsLinearly_WithSegmentCount()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // Tripwire is linux-only: shared-runner scheduling noise dwarfs a 4x segment delta.
        }

        // 4x the segments, identical survivor count at the target width, so the
        // only thing that grows is the work the loop does to get there.
        double six = BestNsPerFit(6, 5);
        double twentyFour = BestNsPerFit(24, 5);
        double ratio = twentyFour / six;

        Console.WriteLine(
            $"#487 fit cost: 6 segments = {six:F0} ns, 24 segments = {twentyFour:F0} ns "
            + $"(ratio {ratio:F2}; linear ~4, the re-summing loop 17 -> 314 lookups, ~18)");

        // Linear gives ~4x for 4x the segments; re-summing gave 18.5x (17 → 314
        // lookups on exactly these two rows). The band sits between them and is
        // twice as wide on the pass side, so a shared runner cannot fail it and a
        // re-summing loop cannot pass it.
        await Assert.That(ratio).IsLessThan(8.0)
            .Because("4x the segments must not cost 18x — Fit carries its total instead of re-summing per victim");
    }

    [Test]
    public async Task Fit_AllocatesNothing_WhenTheRowIsResolvedByDroppingSegments()
    {
        // The drop path allocates nothing: the width table is stack scratch, the
        // survivors are shifted in place, and no segment is re-measured. The
        // character-cut path does allocate (a new string prefix), so the gate
        // covers the shape a painted frame spends most of its time in.
        const int segments = 8;
        StatusSeg[] template = StandardRow(segments);
        int width = StatusBarLayout.TotalWidth(template.AsSpan()[..5]);
        var scratch = new StatusSeg[segments];

        for (int i = 0; i < 20_000; i++)
        {
            template.CopyTo(scratch, 0);
            _ = StatusBarLayout.Fit(scratch, width);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
        {
            template.CopyTo(scratch, 0);
            _ = StatusBarLayout.Fit(scratch, width);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"#487 fit allocations (8 segments, drop-only): {allocated} B over 20 000 calls");

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task WidthCache_LookupsFromFourThreads_AreNotSerialised()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (Environment.ProcessorCount < 4)
        {
            return; // Fewer than four cores: "did it scale" has no answer to check.
        }

        // A box that cannot run four threads at once cannot answer this question,
        // and failing it there would be failing the runner, not the code. Calibrate
        // on pure CPU work first and only assert if the machine demonstrably scales.
        double spinSerial = double.MaxValue;
        double spinParallel = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            spinSerial = Math.Min(spinSerial, RunOn(1, Spin));
            spinParallel = Math.Min(spinParallel, RunOn(4, Spin));
        }

        if (spinParallel > spinSerial * 0.5)
        {
            Console.WriteLine(
                $"#487 width cache: SKIPPED — runner only scales {spinSerial / spinParallel:F2}x on 4 threads (needs 2x)");
            return;
        }

        // Each thread runs a full pass over the same fresh runs, so the four
        // passes are 4x the work of one: a process-global monitor around the
        // measurement serialises them (ratio ~4), per-thread tables do not
        // (ratio ~1).
        double serial = double.MaxValue;
        double parallel = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            string[] runs = NewRuns(round);
            foreach (string run in runs)
            {
                _ = run.GetHashCode(); // prime the string hash, NOT the width cache
            }

            serial = Math.Min(serial, RunOn(1, () => MeasureRuns(runs)));
            parallel = Math.Min(parallel, RunOn(4, () => MeasureRuns(runs)));
        }

        double ratio = parallel / serial;
        Console.WriteLine(
            $"#487 width cache: 1 thread = {serial / 1e6:F1} ms, 4 threads = {parallel / 1e6:F1} ms "
            + $"(ratio {ratio:F2}; a process-global monitor holds the decode and makes this ~4.0)");

        await Assert.That(ratio).IsLessThan(2.0)
            .Because(
                "four threads each doing a full pass of cold width measurements must not queue on a "
                + "shared monitor — the decode is the work, the lock was strictly wider than it");
    }

    /// <summary>Min-of-3 nanoseconds per Fit call, including the row reset.</summary>
    private static double BestNsPerFit(int segments, int keep)
    {
        StatusSeg[] template = StandardRow(segments);
        int width = StatusBarLayout.TotalWidth(template.AsSpan()[..keep]);
        var scratch = new StatusSeg[segments];

        long best = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            // Warm past JIT tier-up: a tier transition charges one-off runtime
            // bookkeeping to the measuring thread and would show up in round one.
            for (int i = 0; i < 5_000; i++)
            {
                template.CopyTo(scratch, 0);
                _ = StatusBarLayout.Fit(scratch, width);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < TimeIterations; i++)
            {
                template.CopyTo(scratch, 0);
                _ = StatusBarLayout.Fit(scratch, width);
            }

            long ticks = Stopwatch.GetTimestamp() - start;
            if (ticks < best)
            {
                best = ticks;
            }
        }

        return (double)best * 1_000_000_000.0 / (Stopwatch.Frequency * TimeIterations);
    }

    /// <summary>
    /// Wall-clock nanoseconds for <paramref name="threads"/> dedicated threads
    /// released together through a barrier, so thread start-up is outside the
    /// measured window and every pass really does overlap.
    /// </summary>
    private static double RunOn(int threads, Func<long> body)
    {
        using var gate = new Barrier(threads + 1);
        var tasks = new Task<long>[threads];
        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Factory.StartNew(
                () =>
                {
                    gate.SignalAndWait(GateTimeout);
                    return body();
                },
                TaskCreationOptions.LongRunning);
        }

        long start = Stopwatch.GetTimestamp();
        gate.SignalAndWait(GateTimeout);
        Task.WaitAll(tasks);

        return (double)(Stopwatch.GetTimestamp() - start) * 1_000_000_000.0 / Stopwatch.Frequency;
    }

    /// <summary>Distinct status-shaped runs, so every lookup is a cold miss.</summary>
    private static string[] NewRuns(int salt)
    {
        var runs = new string[CacheRunsPerThread];
        for (int i = 0; i < runs.Length; i++)
        {
            runs[i] = $"kilocode/model-{salt}-{i} ↑↓ 142 87 $0.0031";
        }

        return runs;
    }

    private static long MeasureRuns(string[] runs)
    {
        long acc = 0;
        foreach (string run in runs)
        {
            acc += UnicodeWidth.WidthCached(run);
        }

        return acc;
    }

    /// <summary>
    /// Pure, data-dependent, thread-local work: the yardstick for whether this
    /// box can run four passes of anything at once.
    /// </summary>
    private static long Spin()
    {
        ulong acc = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < SpinIterations; i++)
        {
            acc = (acc ^ (acc >> 29)) * 0xBF58476D1CE4E5B9UL;
        }

        return (long)acc;
    }
}
