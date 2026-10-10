using System.Text;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// [UX2] #262: universal 10-line collapse + unified expand gesture
// (Enter/click/space) across all Chat/Widgets block types, on top of the
// merged ICollapsibleChatBlock mixin (#313). Rendering reads global
// TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class Ux2CollapseTests
{
    private static (FakeEventBus Bus, ChatTimelinePanel Panel, ChatScreenBridge Bridge) Setup(
        string model = "m")
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = model };
        var bridge = new ChatScreenBridge(bus, panel, status);
        return (bus, panel, bridge);
    }

    private static string LongText(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line-{i:00}"));

    [Test]
    public async Task UserBlock_LongBody_Collapses_WithHiddenTail()
    {
        var block = new UserBlock(LongText(15));
        await Assert.That(block.MaxBodyLines).IsEqualTo(10);
        await Assert.That(block.IsExpanded).IsFalse();
        // Header + 10 body rows + overflow tail + gap row.
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(13);

        var buffer = new ScreenBuffer(40, 13);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 13), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("YOU");
        await Assert.That(art).Contains("line-01");
        await Assert.That(art).Contains("line-10");
        await Assert.That(art).Contains("... (5 hidden)");
        await Assert.That(art).DoesNotContain("line-11");

        block.ToggleExpanded();
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(17);
        var expanded = new ScreenBuffer(40, 17);
        block.Paint(new BlockPaintContext(expanded, new Rect(0, 0, 40, 17), 0));
        string expandedArt = GridDump.Art(expanded);
        await Assert.That(expandedArt).Contains("line-15");
        await Assert.That(expandedArt).DoesNotContain("hidden");
    }

    [Test]
    public async Task UserBlock_ShortBody_PaintsFully()
    {
        var block = new UserBlock("hello world again");
        await Assert.That(block.Measure(14).MinLines).IsEqualTo(4);

        var buffer = new ScreenBuffer(14, 4);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 14, 4), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("again");
        await Assert.That(art).DoesNotContain("hidden");
    }

    [Test]
    public async Task SystemBlock_LongNotice_Collapses_WithHiddenTail()
    {
        var block = new SystemBlock(LongText(12));
        await Assert.That(block.MaxBodyLines).IsEqualTo(10);
        await Assert.That(block.IsExpanded).IsFalse();
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(11);

        var buffer = new ScreenBuffer(40, 11);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 11), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("line-10");
        await Assert.That(art).Contains("... (2 hidden)");
        await Assert.That(art).DoesNotContain("line-11");

        block.SetExpanded(true);
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(12);
    }

    [Test]
    public async Task DiffBlock_LongDiff_Collapses_WithHiddenTail()
    {
        string diff = "diff --git a/f b/f\nindex 111..222 100644\n--- a/f\n+++ b/f\n@@ -1,8 +1,9 @@\n"
            + string.Join("\n", Enumerable.Range(1, 11).Select(i => $" ctx-{i:00}"));
        var block = new DiffBlock(diff);
        await Assert.That(block.MaxBodyLines).IsEqualTo(10);
        await Assert.That(block.IsExpanded).IsFalse();
        // 4 file headers + 1 hunk + 11 context = 16 rows → 10 + tail.
        await Assert.That(block.Measure(60).MinLines).IsEqualTo(11);

        var buffer = new ScreenBuffer(60, 11);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 60, 11), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("ctx-01");
        await Assert.That(art).Contains("... (6 hidden)");
        await Assert.That(art).DoesNotContain("ctx-11");

        block.ToggleExpanded();
        await Assert.That(block.Measure(60).MinLines).IsEqualTo(16);
    }

    [Test]
    public async Task TableBlock_ManyRows_Collapses_KeepingFrame()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(i => (IReadOnlyList<string>)[$"r{i:00}", $"{i}"])
            .ToList();
        var table = new Harbor.Terminal.Abstractions.Rendering.GfmTable(
            ["name", "n"],
            rows,
            [Harbor.Terminal.Abstractions.Rendering.GfmAlign.Left,
             Harbor.Terminal.Abstractions.Rendering.GfmAlign.Right]);
        var block = new TableBlock(table);
        await Assert.That(block.MaxBodyLines).IsEqualTo(10);
        await Assert.That(block.IsExpanded).IsFalse();
        // 4 + 12 = 16 rows → top slice + tail + bottom rule = 11.
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(11);

        var buffer = new ScreenBuffer(40, 11);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 11), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("┌");
        await Assert.That(art).Contains("└");
        await Assert.That(art).Contains("... (6 hidden)");
        await Assert.That(art).DoesNotContain("r12");

        block.ToggleExpanded();
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(16);
        var expanded = new ScreenBuffer(40, 16);
        block.Paint(new BlockPaintContext(expanded, new Rect(0, 0, 40, 16), 0));
        await Assert.That(GridDump.Art(expanded)).Contains("r12");
    }

    [Test]
    public async Task AssistantMarkdownBlock_Defaults_CollapsedWithUniversalBudget()
    {
        var block = new AssistantMarkdownBlock("# Title\ntext line");
        await Assert.That(block.MaxBodyLines).IsEqualTo(10);
        await Assert.That(block.IsExpanded).IsFalse();
        // Short answer: headerless 2 body rows + gap row, as before.
        await Assert.That(block.Measure(20).MinLines).IsEqualTo(3);

        var longBlock = new AssistantMarkdownBlock(LongText(15));
        int collapsed = longBlock.Measure(40).MinLines;
        longBlock.SetExpanded(true);
        int expanded = longBlock.Measure(40).MinLines;
        await Assert.That(collapsed).IsLessThan(expanded);
    }

    [Test]
    public async Task Enter_And_Space_Toggle_Newest_Collapsible_Via_Bridge()
    {
        var (_, panel, bridge) = Setup();
        using (bridge)
        {
            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();

            var user = new UserBlock(LongText(15));
            panel.Timeline.Append(user);

            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
            await Assert.That(user.IsExpanded).IsTrue();

            user.SetExpanded(false);
            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
            await Assert.That(user.IsExpanded).IsTrue();

            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        }
    }

    [Test]
    public async Task Click_On_UserHeader_Toggles_Via_Bridge()
    {
        var (_, panel, bridge) = Setup();
        using (bridge)
        {
            var user = new UserBlock(LongText(15));
            panel.Timeline.Append(user);

            var tl = panel.Timeline;
            tl.PrepareFrame(40, 20);
            tl.Paint(new ScreenBuffer(40, 20), new Rect(0, 0, 40, 20)); // registers the header rect

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Left, 5, 0, EngineInput.KeyModifiers.None))).IsTrue();
            await Assert.That(user.IsExpanded).IsTrue();

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Right, 5, 0, EngineInput.KeyModifiers.None))).IsFalse();
        }
    }
}
