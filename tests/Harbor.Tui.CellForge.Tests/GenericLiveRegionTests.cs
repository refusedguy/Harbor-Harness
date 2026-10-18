using System.Diagnostics;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.TestKit;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GenericLiveRegionTests
{
    private static string Art(GenericLiveRegion region, int width = 20)
    {
        var buffer = new ScreenBuffer(width, Math.Max(1, region.DesiredHeight));
        region.Paint(buffer, new Rect(0, 0, width, buffer.Rows));
        return GridDump.Art(buffer).TrimEnd('\n');
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
        var buffer = new ScreenBuffer(80, 8);
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

    [Test]
    [Timeout(120_000)]
    public async Task Region_Paint_DoubledLines_StaysWithinGrowthLimit()
    {
        // Relative gate t(2N) <= 3*t(N): painting twice the buffered lines
        // must cost at most 3x the time, measured in the same process so
        // runner speed cancels. Quadratic behaviour lands at 4x and cannot
        // hide under the bound. Time-only (no alloc leg): the steady paint is
        // pinned allocation-free by Region_Paint_IsAllocationFree above, and
        // an alloc ratio over "0 / 0 B" would be a number invented for the
        // caller. Measured through Harbor.TestKit's TestScalingGate (#1652):
        // GC-fenced legs scaled toward ~100 ms of real work, medians over five
        // rounds, x1.05 noise band on the time branch — the old best-of-5 over
        // millisecond legs decided real CI runs (the #1649 flake). Linux-only
        // (timing varies by OS load). All lines fit the width, so Fit is the
        // identity and the ratio isolates the windowing itself.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int lineCount = 200;

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

        var small = BuildRegion(lineCount);
        var large = BuildRegion(lineCount * 2);
        var smallBuffer = new ScreenBuffer(80, 500);
        var largeBuffer = new ScreenBuffer(80, 500);
        var smallRect = new Rect(0, 0, 80, 500);
        var largeRect = new Rect(0, 0, 80, 500);

        static TestScalingGateLeg MeasureLegs(GenericLiveRegion region, ScreenBuffer buffer, Rect rect, int units)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < units; i++)
            {
                region.Paint(buffer, rect);
            }

            sw.Stop();
            return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, 0L);
        }

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => MeasureLegs(small, smallBuffer, smallRect, units),
            measureLargeLeg: units => MeasureLegs(large, largeBuffer, largeRect, units),
            initialUnits: 20,
            options: new TestScalingGateOptions
            {
                Label = "liveregion-paint-scaling",
                GrowthLimit = 3.0,
                MeasureAllocations = false,
            });

        await result.AssertWithinLimitsAsync(
            topic: "paint(2N)/paint(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
    }
}
