using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// The delegate seam (#2350): a domain-neutral layout driven by Count /
/// Estimate / Measure / Render, with no chat type in sight. Heights are fixed
/// per index so the visible range has an independent, obvious oracle.
/// </summary>
public class VirtualListDelegateTests
{
    private sealed class FakeDelegate : IVirtualListDelegate
    {
        private readonly int[] _heights;

        public FakeDelegate(params int[] heights) => _heights = heights;

        public int MeasureCalls { get; private set; }
        public int EstimateCalls { get; private set; }
        public List<int> Rendered { get; } = [];
        public List<(int Index, int Y, int Height, int SkipRows)> Rects { get; } = [];

        public int Count => _heights.Length;

        public int EstimateHeight(int index, int width)
        {
            EstimateCalls++;
            return _heights[index];
        }

        public VirtualItemMeasure Measure(int index, int width)
        {
            MeasureCalls++;
            return VirtualItemMeasure.Exact(_heights[index]);
        }

        public void Render(int index, in VirtualItemPaintContext context)
        {
            Rendered.Add(index);
            Rects.Add((index, context.Rect.Y, context.Rect.Height, context.SkipRows));
        }
    }

    /// <summary>Brute-force range over per-item tops; no binary search, no cache.</summary>
    private static (int First, int Last) Oracle(int[] heights, long scrollY, int viewportH)
    {
        if (heights.Length == 0 || viewportH <= 0)
        {
            return (-1, -2);
        }

        int first = -1, last = -1;
        long y = 0;
        for (int i = 0; i < heights.Length; i++)
        {
            long bottom = y + heights[i];
            if (bottom > scrollY && y < scrollY + viewportH)
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }

            y = bottom;
        }

        return (first, last);
    }

    private static int[] Repeated(int count, int height)
    {
        var heights = new int[count];
        Array.Fill(heights, height);
        return heights;
    }

    private static async Task AssertRange(VirtualListLayout layout, long scroll, int viewportH, int first, int last)
    {
        var range = layout.VisibleRange(scroll, viewportH);
        await Assert.That(range.First).IsEqualTo(first);
        await Assert.That(range.Last).IsEqualTo(last);
    }

    [Test]
    public async Task VisibleRange_EmptyDelegate_IsEmpty()
    {
        var layout = new VirtualListLayout();
        var del = new FakeDelegate();
        layout.Attach(del);
        _ = layout.Prepare(40, 10, 0);

        await Assert.That(layout.Count).IsEqualTo(0);
        await Assert.That(layout.TotalHeight).IsEqualTo(0);
        await Assert.That(layout.VisibleRange(0, 10).First).IsEqualTo(-1);
        await Assert.That(layout.VisibleRange(0, 10).Last).IsEqualTo(-2);
    }

    [Test]
    public async Task VisibleRange_FixedHeights_ClampsAtBothEnds()
    {
        var layout = new VirtualListLayout();
        var del = new FakeDelegate(Repeated(100, 1));
        layout.Attach(del);
        _ = layout.Prepare(40, 10, 0);

        await Assert.That(layout.TotalHeight).IsEqualTo(100);
        await AssertRange(layout, 0, 10, 0, 9);
        await AssertRange(layout, 5, 10, 5, 14);
        await AssertRange(layout, 95, 10, 95, 99);
        // Same window rule as TimelineLayoutCache: first is clamped to zero,
        // the end is the raw [scrollY, scrollY + viewportH) span, so a window
        // starting above zero overlaps only rows 0..4.
        await AssertRange(layout, -5, 10, 0, 4);
        // Past the tail the binary search lands on the last item and the raw
        // window still contains its top row.
        await AssertRange(layout, 500, 10, 99, 99);
        await Assert.That(layout.VisibleRange(0, 0).First).IsEqualTo(-1);
    }

    [Test]
    public async Task VisibleRange_VariableHeights_MatchesOracle()
    {
        int[] heights = new int[64];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = i % 3 == 0 ? 4 : (i % 3 == 1 ? 1 : 2);
        }

        var layout = new VirtualListLayout();
        layout.Attach(new FakeDelegate(heights));
        _ = layout.Prepare(40, 7, 0);

        long total = 0;
        foreach (int h in heights)
        {
            total += h;
        }

        await Assert.That(layout.TotalHeight).IsEqualTo(total);
        for (long scroll = 0; scroll <= total; scroll += 3)
        {
            var expected = Oracle(heights, scroll, 7);
            await AssertRange(layout, scroll, 7, expected.First, expected.Last);
        }
    }

    [Test]
    public async Task Prepare_MeasuresOnlyVisibleItems_AndIsStableOnRepeat()
    {
        var del = new FakeDelegate(Repeated(200, 2));
        var layout = new VirtualListLayout();
        layout.Attach(del);
        _ = layout.Prepare(40, 6, 20);

        await Assert.That(del.EstimateCalls).IsEqualTo(200);
        // Rows [20..26) cover items 10, 11 and 12 at two rows each.
        await Assert.That(del.MeasureCalls).IsEqualTo(3);

        int before = del.MeasureCalls;
        _ = layout.Prepare(40, 6, 20);
        await Assert.That(del.MeasureCalls).IsEqualTo(before);
    }

    [Test]
    public async Task Paint_RendersOnlyVisibleRange_WithClipAndSkipRows()
    {
        var del = new FakeDelegate(Repeated(50, 2));
        var layout = new VirtualListLayout();
        layout.Attach(del);
        _ = layout.Prepare(40, 5, 4);

        var buffer = new ScreenBuffer(20, 5);
        layout.Paint(buffer, new Rect(0, 0, 20, 5), scrollY: 4, tick: 1);

        // Rows [4..9): items 2, 3 and 4; the last one is clipped by the rect.
        await Assert.That(del.Rendered).IsEquivalentTo(new[] { 2, 3, 4 });
        await Assert.That(del.Rects[0].SkipRows).IsEqualTo(0);
        await Assert.That(del.Rects[0].Y).IsEqualTo(0);
        await Assert.That(del.Rects[1].Y).IsEqualTo(2);
        await Assert.That(del.Rects[2].Height).IsEqualTo(1);
    }

    [Test]
    public async Task CountChange_ExtendsRangeWithoutRemeasuringExistingItems()
    {
        var del = new FakeDelegate(Repeated(10, 1));
        var layout = new VirtualListLayout();
        layout.Attach(del);
        _ = layout.Prepare(40, 4, 0);
        int measuredAfterFirst = del.MeasureCalls;

        var grown = new FakeDelegate(Repeated(30, 1));
        layout.Attach(grown);
        _ = layout.Prepare(40, 4, 0);

        await Assert.That(layout.Count).IsEqualTo(30);
        await Assert.That(layout.TotalHeight).IsEqualTo(30);
        await AssertRange(layout, 0, 4, 0, 3);
        // Rebinding to a different source resets measurements; the old source
        // must not be consulted again.
        await Assert.That(del.MeasureCalls).IsEqualTo(measuredAfterFirst);
        await Assert.That(grown.MeasureCalls).IsEqualTo(4);
    }

    [Test]
    public async Task ChatTimeline_IsUsableAsDelegate_SameVisibleRange()
    {
        var tl = new VirtualizedChatTimeline();
        for (int i = 0; i < 40; i++)
        {
            tl.Append(new SystemBlock($"line {i}"));
        }

        _ = tl.PrepareFrame(40, 8);
        tl.ScrollToTop();
        _ = tl.PrepareFrame(40, 8);

        var layout = new VirtualListLayout();
        layout.Attach(tl);
        _ = layout.Prepare(40, 8, tl.ScrollY);

        await Assert.That(layout.Count).IsEqualTo(40);
        var chatRange = tl.VisibleRange(8);
        await AssertRange(layout, tl.ScrollY, 8, chatRange.First, chatRange.Last);

        // The neutral render path reaches the same blocks as the chat one.
        var buffer = new ScreenBuffer(40, 8);
        layout.Paint(buffer, new Rect(0, 0, 40, 8), tl.ScrollY, tick: 1);
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'l');
    }

    [Test]
    public async Task VirtualItemMeasure_FloorsEstimateAtOneRow()
    {
        var estimate = VirtualItemMeasure.Estimate(0, 0);
        await Assert.That(estimate.BestGuess).IsEqualTo(1);
        await Assert.That(VirtualItemMeasure.Exact(7).BestGuess).IsEqualTo(7);
    }
}
