using System.Diagnostics;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.TestKit;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GenericLiveRegionTests
{
    /// <summary>Paints per measured leg - the FIXED amount of work.</summary>
    /// <remarks>
    ///     400 paints of a 400-line region is about 47 ms of real work on the
    ///     CI runner.
    /// </remarks>
    private const int PaintsPerLeg = 400;

    /// <summary>
    ///     Allocation budget for ONE painted frame, in bytes. The steady paint
    ///     allocates NOTHING, so the budget is the exact zero - the strongest
    ///     shape a byte claim can take, and the one
    ///     <c>Region_Paint_IsAllocationFree</c> above already pins. A
    ///     per-frame snapshot (the #1100 regression) allocates kilobytes and
    ///     cannot hide under it.
    /// </summary>
    private const long MaxBytesPerPaint = 0;

    /// <summary>
    ///     Time budget for ONE painted frame, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 17.099 ms over 287 paints of a 200-line region, and
    ///     1.99x that for the 400-line one, i.e. ~312 probe units per frame
    ///     against a probe that costs 0.000380 ms/unit.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerPaint = 4_500.0;

    private static string Art(GenericLiveRegion region, int width = 20)
    {
        var buffer = TestRender.Buffer(width, Math.Max(1, region.DesiredHeight));
        region.Paint(buffer, new Rect(0, 0, width, buffer.Rows));
        return TestRender.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task Task_Ratio_ClampsAndGuardsUnknownTotal()
    {
        await Assert.That(new LiveProgressTask(5, 10).Ratio).IsEqualTo(0.5);
        await Assert.That(new LiveProgressTask(-2, 10).Ratio).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(12, 10).Ratio).IsEqualTo(1);
        await Assert.That(new LiveProgressTask(1, 0).Ratio).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(1, -4).Ratio).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(double.NaN, 10).Ratio).IsEqualTo(0);
    }

    [Test]
    public async Task Task_Remaining_NeverNegative()
    {
        await Assert.That(new LiveProgressTask(3, 10).Remaining).IsEqualTo(7);
        await Assert.That(new LiveProgressTask(12, 10).Remaining).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(3, 0).Remaining).IsEqualTo(0);
    }

    [Test]
    public async Task Task_Labels_ReadPercentAndCount()
    {
        await Assert.That(new LiveProgressTask(0, 10).PercentText).IsEqualTo("0%");
        await Assert.That(new LiveProgressTask(5, 10).PercentText).IsEqualTo("50%");
        await Assert.That(new LiveProgressTask(3, 10).CountText).IsEqualTo("3/10");
        await Assert.That(new LiveProgressTask(3, 0).CountText).IsEqualTo("3/?");
        await Assert.That(new LiveProgressTask(10, 10).IsCompleted).IsTrue();
        await Assert.That(new LiveProgressTask(9, 10).IsCompleted).IsFalse();
    }

    [Test]
    public async Task Region_ClipHead_KeepsNewest()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipHead };
        region.Update(["a", "b", "c"]);
        await Assert.That(region.VisibleLines()).IsEquivalentTo(["b", "c"]);
        await Assert.That(region.RenderPlainText()).IsEqualTo("b\nc");
    }

    [Test]
    public async Task Region_ClipTail_KeepsOldest()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipTail };
        region.Update(["a", "b", "c"]);
        await Assert.That(region.VisibleLines()).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Region_Ellipsize_SpendsLastRowOnMarker()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.Ellipsize };
        region.Update(["a", "b", "c"]);
        IReadOnlyList<string> visible = region.VisibleLines();
        await Assert.That(visible.Count).IsEqualTo(2);
        await Assert.That(visible[0]).IsEqualTo("a");
        await Assert.That(visible[1].Contains("more")).IsTrue();
    }

    [Test]
    public async Task Region_AutoClear_EmptiesOnComplete()
    {
        var clearing = new GenericLiveRegion { AutoClear = true };
        clearing.Update("hello");
        clearing.Complete();
        await Assert.That(clearing.LineCount).IsEqualTo(0);

        var keeping = new GenericLiveRegion { AutoClear = false };
        keeping.Update("hello");
        keeping.Complete();
        await Assert.That(keeping.LineCount).IsEqualTo(1);
    }

    [Test]
    public async Task Region_MultilineUpdate_SplitsAndIgnoresTrailingNewline()
    {
        var region = new GenericLiveRegion();
        region.Update("a\nb\n");
        await Assert.That(region.VisibleLines()).IsEquivalentTo(["a", "b"]);
        await Assert.That(region.DesiredHeight).IsEqualTo(2);
    }

    [Test]
    public async Task Columns_Bar_MatchesProgressMath()
    {
        string bar = LiveColumns.Bar(new LiveProgressTask(5, 10), 10);
        await Assert.That(bar).IsEqualTo(new string(LiveColumns.Filled, 5) + new string(LiveColumns.Track, 5));
        await Assert.That(LiveColumns.Bar(new LiveProgressTask(0, 10), 4)).IsEqualTo(new string(LiveColumns.Track, 4));
        await Assert.That(LiveColumns.Bar(new LiveProgressTask(1, 10), 0)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Columns_Join_DropsEmpty()
    {
        var task = new LiveProgressTask(3, 10);
        string line = LiveColumns.Join(" ", LiveColumns.Spinner(0), string.Empty, LiveColumns.Percent(task));
        await Assert.That(line.Contains("30%")).IsTrue();
        await Assert.That(line.Contains("  ")).IsFalse();
        await Assert.That(LiveColumns.Spinner(0)).IsEqualTo("⠋");
        await Assert.That(LiveColumns.Spinner(10)).IsEqualTo("⠋");
    }

    [Test]
    public async Task Region_Paint_ShowsVisibleWindow()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipHead };
        region.Update(["a", "b", "c"]);
        string art = Art(region);
        await Assert.That(art.Contains("b")).IsTrue();
        await Assert.That(art.Contains("c")).IsTrue();
        await Assert.That(art.Contains("a")).IsFalse();
    }

    [Test]
    public async Task Region_Paint_MatchesVisibleLinesInEveryMode()
    {
        // Snapshot-free Paint must draw exactly the VisibleLines window in
        // every overflow mode (plus the untouched small case).
        var head = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipHead };
        head.Update(["a", "b", "c"]);
        string headArt = Art(head, 20);
        await Assert.That(headArt.Contains("b")).IsTrue();
        await Assert.That(headArt.Contains("c")).IsTrue();
        await Assert.That(headArt.Contains("a")).IsFalse();

        var tail = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipTail };
        tail.Update(["a", "b", "c"]);
        string tailArt = Art(tail, 20);
        await Assert.That(tailArt.Contains("a")).IsTrue();
        await Assert.That(tailArt.Contains("b")).IsTrue();
        await Assert.That(tailArt.Contains("c")).IsFalse();

        var ellipsize = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.Ellipsize };
        ellipsize.Update(["a", "b", "c"]);
        string ellipsizeArt = Art(ellipsize, 20);
        await Assert.That(ellipsizeArt.Contains("a")).IsTrue();
        await Assert.That(ellipsizeArt.Contains("more")).IsTrue();

        var small = new GenericLiveRegion { MaxHeight = 8, Overflow = LiveRegionOverflow.ClipHead };
        small.Update(["x", "y"]);
        string smallArt = Art(small, 20);
        await Assert.That(smallArt.Contains("x")).IsTrue();
        await Assert.That(smallArt.Contains("y")).IsTrue();
    }

    [Test]
    public async Task Region_Paint_IsAllocationFree()
    {
        // Issue #1100 (per-frame snapshots): Paint called VisibleLines(), which
        // ToArray-copied _lines on every frame. Steady paints must allocate
        // nothing (same shape as MascotPanelTests.PanelPaint_IsAllocationFree:
        // warm up tiered-JIT paths, then count thread bytes over 200 paints).
        var region = new GenericLiveRegion { MaxHeight = 8, Overflow = LiveRegionOverflow.ClipHead };
        region.Update(["alpha", "bravo", "charlie", "delta"]);
        var buffer = TestRender.Buffer(80, 8);
        var rect = new Rect(0, 0, 80, 8);

        for (int i = 0; i < 20; i++)
        {
            region.Paint(buffer, rect);
        }

        GC.WaitForPendingFinalizers();

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            region.Paint(buffer, rect);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0)
            .Because("per-frame Paint must read _lines by index instead of snapshotting it per frame.");
    }

    /// <summary>
    ///     THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
    ///     to be <c>t(2N) &lt;= 3*t(N)</c> over 200 against 400 buffered lines,
    ///     decided by the shared TestScalingGate (#1652), and it reddened on a
    ///     real CI run at 3.54 against the 3.15 limit. It is now:
    ///     <code>
    ///     bytes per painted frame  &lt;= 0      (machine-independent, HARD)
    ///     probe units per frame    &lt;= 4 500  (machine-scaled time, SOFT)
    ///     </code>
    ///     measured over a FIXED amount of work (400 paints of a 400-line
    ///     region) on the SAME runner in the SAME run. The allocation branch is
    ///     machine-independent and is asserted hard - the steady paint
    ///     allocates NOTHING, and a per-frame snapshot (the #1100 regression)
    ///     allocates kilobytes. The time branch is in PROBE UNITS and stands
    ///     down when the rounds do not reproduce each other. The report prints
    ///     on every run, pass or fail. Linux-only (timing varies by OS load).
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task Region_Paint_CostPerFrame_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int lineCount = 400;

        static GenericLiveRegion BuildRegion(int lines)
        {
            var region = new GenericLiveRegion { MaxHeight = 500, Overflow = LiveRegionOverflow.ClipHead };
            var items = new string[lines];
            for (int i = 0; i < lines; i++)
            {
                items[i] = $"line_{i:0000}_abcdef";
            }

            region.Update(items);
            return region;
        }

        var region = BuildRegion(lineCount);
        var buffer = TestRender.Buffer(80, 500);
        var rect = new Rect(0, 0, 80, 500);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: units => Task.FromResult(MeasurePaints(region, buffer, rect, units)),
            units: PaintsPerLeg,
            options: new PerfGateOptions
            {
                Label = "liveregion-paint-cost",
                MaxBytesPerUnit = MaxBytesPerPaint,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerPaint,

                // A steady paint legitimately allocates NOTHING, so zero is the
                // correct answer here and not a missing measurement.
                RequirePositiveBytes = false,
            });

        await result.AssertWithinBudgetsAsync("live region paint per frame");
    }

    /// <summary>
    ///     One measured leg: <paramref name="paints" /> paints of the same
    ///     region into the same buffer, timed and allocation-counter'd as one
    ///     window. The stopwatch is created BEFORE the counter is read, so its
    ///     own allocation stays out of the exact-zero window.
    /// </summary>
    private static PerfGateLeg MeasurePaints(GenericLiveRegion region, ScreenBuffer buffer, Rect rect, int paints)
    {
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < paints; i++)
        {
            region.Paint(buffer, rect);
        }

        sw.Stop();
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
