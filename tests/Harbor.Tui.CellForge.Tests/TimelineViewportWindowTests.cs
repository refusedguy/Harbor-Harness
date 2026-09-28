using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG8 #279 (btea viewport pattern): the timeline owns a
/// <see cref="ScrollableViewport"/> window and <see cref="VirtualizedChatTimeline.Paint"/>
/// renders only the visible slice (offset + clipped rows) of a long timeline —
/// never the whole buffer. Same shape as <see cref="CommandPaletteView.Viewport"/>.
/// </summary>
public class TimelineViewportWindowTests
{
    private sealed class PaintCountingBlock : IChatBlock
    {
        public int PaintCalls;

        public string Kind => "probe";
        public bool IsStreamContinuation => false;
        public int BudgetBytes => 64;
        public BlockMeasure Measure(int width) => BlockMeasure.Exact(1);
        public int CheapEstimate(int width) => 1;

        public void Paint(in BlockPaintContext ctx)
        {
            PaintCalls++;
            ctx.Buffer.SetText(ctx.Rect.X, ctx.Rect.Y, "x", CellStyle.Plain);
        }

        public string RawText() => "x";
    }

    private static (VirtualizedChatTimeline Timeline, List<PaintCountingBlock> Blocks) LongTimeline(int count)
    {
        var tl = new VirtualizedChatTimeline();
        var blocks = new List<PaintCountingBlock>(count);
        for (int i = 0; i < count; i++)
        {
            var b = new PaintCountingBlock();
            blocks.Add(b);
            tl.Append(b);
        }

        return (tl, blocks);
    }

    [Test]
    public async Task LongTimeline_PaintsOnlyVisibleSlice()
    {
        var (tl, blocks) = LongTimeline(300);
        var buffer = new ScreenBuffer(40, 10);
        _ = tl.PrepareFrame(40, 10); // follow-tail: rows 290..300
        tl.Paint(buffer, new Rect(0, 0, 40, 10));

        for (int i = 0; i < 290; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(0);
        }

        for (int i = 290; i < 300; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Paint_ClipsToRectHeight()
    {
        var (tl, blocks) = LongTimeline(300);
        var buffer = new ScreenBuffer(40, 10);
        _ = tl.PrepareFrame(40, 10); // ScrollY = 290
        tl.Paint(buffer, new Rect(0, 0, 40, 4)); // narrower window: rows 290..294

        for (int i = 0; i < 290; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(0);
        }

        for (int i = 290; i < 294; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(1);
        }

        for (int i = 294; i < 300; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ScrolledWindow_PaintsMiddleSliceOnly()
    {
        var (tl, blocks) = LongTimeline(300);
        var buffer = new ScreenBuffer(40, 10);
        _ = tl.PrepareFrame(40, 10);

        tl.ScrollUp(280); // 290 -> 10, unpins
        _ = tl.PrepareFrame(40, 10);
        await Assert.That(tl.FollowTail).IsFalse();
        await Assert.That(tl.ScrollY).IsEqualTo(10L);

        tl.Paint(buffer, new Rect(0, 0, 40, 10));

        for (int i = 0; i < 10; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(0);
        }

        for (int i = 10; i < 20; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(1);
        }

        for (int i = 20; i < 300; i++)
        {
            await Assert.That(blocks[i].PaintCalls).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Viewport_MirrorsScrollGeometry()
    {
        var (tl, _) = LongTimeline(300);
        _ = tl.PrepareFrame(40, 10);

        await Assert.That(tl.Viewport.TotalContent).IsEqualTo(tl.TotalHeight);
        await Assert.That(tl.Viewport.ViewportH).IsEqualTo(10);
        await Assert.That(tl.Viewport.Offset).IsEqualTo(tl.ScrollY);
        await Assert.That(tl.Viewport.MaxOffset).IsEqualTo(Math.Max(0, tl.TotalHeight - 10));

        var (first, last) = tl.Viewport.VisibleSlice();
        await Assert.That(first).IsEqualTo(tl.ScrollY);
        await Assert.That(last).IsEqualTo(Math.Min(tl.TotalHeight, tl.ScrollY + 10));
    }

    [Test]
    public async Task Viewport_ScrollPathsStayInWindow()
    {
        var (tl, _) = LongTimeline(300);
        _ = tl.PrepareFrame(40, 10);

        tl.ScrollToEnd(viewportHeight: 10);
        _ = tl.PrepareFrame(40, 10);
        await Assert.That(tl.Viewport.AtBottom).IsTrue();
        await Assert.That(tl.Viewport.Offset).IsEqualTo(tl.ScrollY);

        tl.ScrollToTop();
        _ = tl.PrepareFrame(40, 10);
        await Assert.That(tl.Viewport.Offset).IsEqualTo(0L);
        await Assert.That(tl.Viewport.AtTop).IsTrue();

        tl.ScrollBy(5);
        _ = tl.PrepareFrame(40, 10);
        await Assert.That(tl.Viewport.Offset).IsEqualTo(5L);
        await Assert.That(tl.Viewport.Offset).IsEqualTo(tl.ScrollY);
    }

    [Test]
    public async Task Clear_ResetsViewportWindow()
    {
        var (tl, _) = LongTimeline(300);
        _ = tl.PrepareFrame(40, 10);
        await Assert.That(tl.Viewport.TotalContent).IsEqualTo(300L);

        tl.Clear();

        await Assert.That(tl.Viewport.TotalContent).IsEqualTo(0L);
        await Assert.That(tl.Viewport.Offset).IsEqualTo(0L);
    }
}
