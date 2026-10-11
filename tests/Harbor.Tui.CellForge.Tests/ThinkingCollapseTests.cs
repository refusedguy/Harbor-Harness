using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// O10 #1179 (opencode ReasoningPart steal): thinking is collapsed to a single
// summary line plus duration by default — one row throughout, so the layout
// never shifts; click / feed Enter expands through the existing
// ICollapsibleChatBlock gesture. The 3-stage expand (collapsed → expanded →
// full, [UX4] #264) lives on unchanged for the expanded body.
public class ThinkingCollapseTests
{
    private static string LongThinking(int lines)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= lines; i++)
        {
            sb.Append("reasoning line ").Append(i).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    [Test]
    public async Task Defaults_AreCollapsedOneLineSummary()
    {
        var streaming = new StreamingThinkingBlock();
        await Assert.That(streaming.IsExpanded).IsFalse();
        await Assert.That(streaming.IsFullyExpanded).IsFalse();
        await Assert.That(streaming.Measure(80).MinLines).IsEqualTo(1);
        await Assert.That(streaming.CheapEstimate(80)).IsEqualTo(1);

        var final = new ThinkingBlock("short");
        await Assert.That(final.IsExpanded).IsFalse();
        await Assert.That(final.IsFullyExpanded).IsFalse();
        await Assert.That(final.Measure(80).MinLines).IsEqualTo(1);
        await Assert.That(final.CheapEstimate(80)).IsEqualTo(1);

        // Expanded-stage budgets are unchanged (10 / 20 / full).
        await Assert.That(streaming.MaxBodyLines).IsEqualTo(10);
        await Assert.That(final.MaxBodyLines).IsEqualTo(10);
    }

    [Test]
    public async Task Collapsed_ShowsSingleSummaryLine()
    {
        var block = new ThinkingBlock(LongThinking(30));
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);
        await Assert.That(block.CheapEstimate(80)).IsEqualTo(1);

        var buffer = new ScreenBuffer(80, 1);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 1), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("+ Thought: reasoning line 1")).IsTrue();
        await Assert.That(art.Contains("reasoning line 11")).IsFalse();
        await Assert.That(art.Contains("…")).IsFalse();
    }

    [Test]
    public async Task Collapsed_HeaderCarriesDuration()
    {
        var block = new ThinkingBlock("weighing the options", TimeSpan.FromSeconds(2.5));
        var buffer = new ScreenBuffer(80, 1);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 1), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("+ Thought: weighing the options · 2.5s")).IsTrue();
    }

    [Test]
    public async Task Collapsed_BoldTitleBecomesSummary()
    {
        var block = new ThinkingBlock("**Inspecting PR workflow**\n\nFirst step\nSecond step");
        var buffer = new ScreenBuffer(80, 1);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 1), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("+ Thought: Inspecting PR workflow")).IsTrue();
        await Assert.That(art.Contains("First step")).IsFalse();
    }

    [Test]
    public async Task Streaming_CollapsedStaysOneLine()
    {
        var block = new StreamingThinkingBlock();
        block.Append("first thought\nsecond thought\nthird thought\n");
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);

        var buffer = new ScreenBuffer(80, 1);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 1), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("+ Thinking: first thought")).IsTrue();
        await Assert.That(art.Contains("second thought")).IsFalse();
    }

    [Test]
    public async Task ExpandedBody_StaysByteIdentical()
    {
        var block = new ThinkingBlock("one\ntwo\nthree");
        block.SetExpanded(true);
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(3);

        var streaming = new StreamingThinkingBlock();
        streaming.Append("one\ntwo\nthree");
        streaming.SetExpanded(true);
        await Assert.That(streaming.Measure(80).MinLines).IsEqualTo(3);
    }

    [Test]
    public async Task CycleExpand_WalksThreeStages()
    {
        var block = new ThinkingBlock(LongThinking(30));
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);

        block.CycleExpand();
        await Assert.That(block.IsExpanded).IsTrue();
        await Assert.That(block.IsFullyExpanded).IsFalse();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(21);

        block.CycleExpand();
        await Assert.That(block.IsFullyExpanded).IsTrue();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(30);

        var buffer = new ScreenBuffer(80, 30);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 30), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("reasoning line 30")).IsTrue();
        await Assert.That(art.Contains("…")).IsFalse();

        block.CycleExpand();
        await Assert.That(block.IsExpanded).IsFalse();
        await Assert.That(block.IsFullyExpanded).IsFalse();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);
    }

    [Test]
    public async Task ToggleExpanded_StaysTwoStageCompat()
    {
        var block = new ThinkingBlock(LongThinking(30));
        block.ToggleExpanded();
        await Assert.That(block.IsExpanded).IsTrue();
        await Assert.That(block.IsFullyExpanded).IsFalse();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(21);

        block.ToggleExpanded();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);

        block.SetExpanded(true);
        await Assert.That(block.IsFullyExpanded).IsFalse();
        block.SetExpanded(false);
        await Assert.That(block.IsExpanded).IsFalse();
    }

    [Test]
    public async Task Streaming_CheapEstimate_MirrorsMeasure()
    {
        var block = new StreamingThinkingBlock();
        block.Append(LongThinking(30));
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);
        await Assert.That(block.CheapEstimate(80)).IsEqualTo(1);

        block.CycleExpand();
        block.CycleExpand();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(30);
        await Assert.That(block.CheapEstimate(80)).IsEqualTo(30);
    }
}
