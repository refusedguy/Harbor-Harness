using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class VirtualizedChatTimelineTests
{
    private sealed class FixedBlock : IChatBlock
    {
        public FixedBlock(string text, int lines) => Text = text;
        public string Text { get; }
        public string Kind => "fixed";
        public bool IsStreamContinuation => false;
        public int BudgetBytes => 64;
        public BlockMeasure Measure(int width) => BlockMeasure.Exact(1);
        public int CheapEstimate(int width) => 1;

        public void Paint(in BlockPaintContext ctx)
        {
            ctx.Buffer.SetText(ctx.Rect.X, ctx.Rect.Y, Text, CellStyle.Plain);
        }

        public string RawText() => Text;
    }

    [Test]
    public async Task FollowTail_SticksToBottomOnAppend()
    {
        var tl = new VirtualizedChatTimeline();
        for (int i = 0; i < 30; i++)
        {
            tl.Append(new UserBlock($"m{i}"));
            _ = tl.PrepareFrame(width: 40, viewportH: 10);
        }

        await Assert.That(tl.FollowTail).IsTrue();
        await Assert.That(tl.ScrollY).IsEqualTo(tl.TotalHeight - 10);
        await Assert.That(tl.BlockAt(29).RawText()).Contains("m29");
    }

    [Test]
    public async Task ScrollUp_Unpins_AppendKeepsViewport_ScrollToEndRepins()
    {
        var tl = new VirtualizedChatTimeline();
        for (int i = 0; i < 20; i++)
        {
            tl.Append(new SystemBlock($"line {i}"));
        }

        _ = tl.PrepareFrame(40, 5);

        tl.ScrollUp(7);
        long pinnedY = tl.ScrollY;
        await Assert.That(tl.FollowTail).IsFalse();

        tl.Append(new SystemBlock("new arrival"));
        _ = tl.PrepareFrame(40, 5);
        await Assert.That(tl.ScrollY).IsEqualTo(0); // view resets on append when not following tail

        tl.ScrollToEnd(viewportHeight: 5);
        _ = tl.PrepareFrame(40, 5);
        await Assert.That(tl.FollowTail).IsTrue();
        await Assert.That(tl.BlockAt(tl.Count - 1).RawText()).IsEqualTo("new arrival");
    }

    [Test]
    public async Task Paint_TouchesOnlyViewportRect()
    {
        var buffer = new ScreenBuffer(40, 12);
        buffer.FillAll(Cell.From(new Rune('.'), CellStyle.Plain));

        var tl = new VirtualizedChatTimeline();
        tl.Append(new FixedBlock("TOP", 1));
        for (int i = 0; i < 10; i++)
        {
            tl.Append(new FixedBlock($"mid{i}", 1));
        }
        tl.Append(new FixedBlock("BOTTOM", 1));
        _ = tl.PrepareFrame(40, 4); // viewport = last 4 rows

        // Rect is the bottom slice of a taller screen: rows 8..11, cols 2..31.
        // Viewport shows the last 4 blocks: mid7..mid9 + BOTTOM.
        var rect = new Rect(2, 8, 30, 4);
        tl.Paint(buffer, rect);

        string art = GridDump.Art(buffer);
        var rows = art.Split('\n');

        // Rows above the rect stay untouched filler.
        await Assert.That(rows[0].Contains("TOP")).IsFalse();
        await Assert.That(rows[7].Contains("mid")).IsFalse();
        // First visible row paints inside the rect columns.
        await Assert.That(rows[8]).DoesNotStartWith("mid");   // col 0-1 untouched
        await Assert.That(rows[8].Contains("mid7")).IsTrue();
        await Assert.That(rows[11].Contains("BOTTOM")).IsTrue();
        // Nothing painted beyond the rect's right edge.
        await Assert.That(rows[11].Substring(32)).IsEqualTo("........");
    }

    [Test]
    public async Task WidthChange_KeepsAnchorBlockStable()
    {
        var tl = new VirtualizedChatTimeline();
        for (int i = 0; i < 40; i++)
        {
            tl.Append(new AssistantMarkdownBlock($"block number {i} with some words to wrap"));
        }

        tl.ScrollUp(1000); // unpin & go somewhere mid-history
        _ = tl.PrepareFrame(60, 8);
        int topBefore = tl.VisibleRange(8).First;
        var anchorText = tl.BlockAt(topBefore).RawText();

        _ = tl.PrepareFrame(25, 8);          // width change
        int topAfter = tl.VisibleRange(8).First;
        var afterText = tl.BlockAt(topAfter).RawText();

        await Assert.That(afterText).IsEqualTo(anchorText);
    }

    [Test]
    public async Task Budget_EvictsOldestButNeverTheNewest()
    {
        var tl = new VirtualizedChatTimeline { BudgetBytes = 200 };
        for (int i = 0; i < 50; i++)
        {
            tl.Append(new UserBlock(new string('x', 60)));
        }

        await Assert.That(tl.Count).IsLessThan(50);
        await Assert.That(tl.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(tl.PrepareFrame(40, 6)).IsNotEqualTo(LayoutOutcome.Unchanged);
    }

    [Test]
    public async Task ReplaceLast_CommitsStreamPlaceholder()
    {
        var tl = new VirtualizedChatTimeline();
        var stream = new StreamingMarkdownBlock();
        stream.Push("# streaming ");
        stream.Push("answer");
        tl.Append(stream);
        _ = tl.PrepareFrame(40, 6);

        tl.ReplaceLast(new AssistantMarkdownBlock("# done\n"));
        _ = tl.PrepareFrame(40, 6);

        await Assert.That(tl.Count).IsEqualTo(1);
        await Assert.That(tl.TotalHeight).IsEqualTo(1);
        await Assert.That(tl.BlockAt(0).Kind).IsEqualTo("assistant");
    }
}
