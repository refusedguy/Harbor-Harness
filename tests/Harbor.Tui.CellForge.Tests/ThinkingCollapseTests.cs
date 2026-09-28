using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// [UX4] #264: thinking as collapsed box (crush pattern) — reasoning streams
// into a default-collapsed 10-line box, never interleaved with the final
// answer; 3-stage expand (collapsed → expanded → full) lives block-side.
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
    public async Task Defaults_AreCollapsedTenLineBox()
    {
        var streaming = new StreamingThinkingBlock();
        await Assert.That(streaming.IsExpanded).IsFalse();
        await Assert.That(streaming.IsFullyExpanded).IsFalse();
        await Assert.That(streaming.MaxBodyLines).IsEqualTo(10);

        var final = new ThinkingBlock("short");
        await Assert.That(final.IsExpanded).IsFalse();
        await Assert.That(final.IsFullyExpanded).IsFalse();
        await Assert.That(final.MaxBodyLines).IsEqualTo(10);
    }

    [Test]
    public async Task ShortBlock_StaysByteIdentical()
    {
        var block = new ThinkingBlock("one\ntwo\nthree");
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(3);

        var streaming = new StreamingThinkingBlock();
        streaming.Append("one\ntwo\nthree");
        await Assert.That(streaming.Measure(80).MinLines).IsEqualTo(3);
    }

    [Test]
    public async Task Collapsed_ShowsTenPlusMarker()
    {
        var block = new ThinkingBlock(LongThinking(30));
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(11);

        var buffer = new ScreenBuffer(80, 11);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 11), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("reasoning line 10")).IsTrue();
        await Assert.That(art.Contains("reasoning line 11")).IsFalse();
        await Assert.That(art.Contains("…")).IsTrue();
    }

    [Test]
    public async Task CycleExpand_WalksThreeStages()
    {
        var block = new ThinkingBlock(LongThinking(30));
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(11);

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
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(11);
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
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(11);

        block.SetExpanded(true);
        await Assert.That(block.IsFullyExpanded).IsFalse();
        block.SetExpanded(false);
        await Assert.That(block.IsExpanded).IsFalse();
    }

    [Test]
    public async Task Streaming_CheapEstimate_MirrorsCollapsedMeasure()
    {
        var block = new StreamingThinkingBlock();
        block.Append(LongThinking(30));
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(11);
        await Assert.That(block.CheapEstimate(80)).IsLessThanOrEqualTo(11);

        block.CycleExpand();
        block.CycleExpand();
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(30);
        await Assert.That(block.CheapEstimate(80)).IsEqualTo(30);
    }
}
