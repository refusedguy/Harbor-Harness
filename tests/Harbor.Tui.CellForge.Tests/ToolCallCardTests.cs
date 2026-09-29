using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Tool-call cards in the CellForge feed (Avalonia ToolCallCardView parity):
// collapsed header (status icon + tool name + short args + duration),
// Enter/click expand (full args + truncated result), status + duration from
// the ToolExecutionStart/End pair. Rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class ToolCallCardTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static (FakeEventBus Bus, ChatTimelinePanel Panel, ChatScreenBridge Bridge) Setup(
        string model = "m")
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = model };
        var bridge = new ChatScreenBridge(bus, panel, status);
        return (bus, panel, bridge);
    }

    [Test]
    public async Task Card_Builds_From_Start_And_End_Ok()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", Args("{\"path\":\"src/a.cs\"}")));
            bridge.Tick(120);
            await bus.PublishAsync(new ToolExecutionEndEvent("tc1", ToolResult.Success("file body"), IsError: false));

            await Assert.That(panel.Timeline.Count).IsEqualTo(1);
            var card = (ToolCallBlock)panel.Timeline.BlockAt(0);
            await Assert.That(card.Kind).IsEqualTo("tool-call");
            await Assert.That(card.Status).IsEqualTo(ToolCallStatus.Ok);
            await Assert.That(card.Body.Value.Output).IsEqualTo("file body");
            await Assert.That(card.Body.Value.Duration).IsEqualTo(TimeSpan.FromMilliseconds(120));
            await Assert.That(card.IsExpanded).IsFalse();
        }
    }

    [Test]
    public async Task Card_Builds_From_Start_And_End_Error()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await bus.PublishAsync(new ToolExecutionStartEvent("tc9", "bash", Args("{}")));
            bridge.Tick(50);
            await bus.PublishAsync(new ToolExecutionEndEvent("tc9", ToolResult.Error("exit code 1"), IsError: true));

            var card = (ToolCallBlock)panel.Timeline.BlockAt(panel.Timeline.Count - 1);
            await Assert.That(card.Status).IsEqualTo(ToolCallStatus.Error);
            await Assert.That(card.Body.Value.Output).IsEqualTo("exit code 1");

            var buffer = new ScreenBuffer(40, 2);
            card.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));
            await Assert.That(GridDump.Art(buffer)).Contains("✖ bash (50ms)");
        }
    }

    [Test]
    public async Task ToggleExpanded_Flips_State_And_Grows_Measure()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "make"));
        await Assert.That(block.IsExpanded).IsFalse();
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(1);

        block.ToggleExpanded();
        await Assert.That(block.IsExpanded).IsTrue();
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(2); // header + full-args row

        var buffer = new ScreenBuffer(40, 2);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));
        await Assert.That(GridDump.Art(buffer)).Contains("args:");

        block.SetExpanded(false);
        await Assert.That(block.IsExpanded).IsFalse();
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(1);
    }

    [Test]
    public async Task Collapsed_Shows_Preview_Expanded_Shows_Full_Args_And_More_Result()
    {
        var block = new ToolCallBlock(new ToolCallInfo(
            "t1", "read", "src/a.cs", ArgsFull: "{\"path\":\"src/a.cs\",\"limit\":200}"))
        {
            MaxBodyLines = 2,
        };
        string output = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}"));
        block.Complete(new ToolResultBody(output, isError: false, TimeSpan.FromMilliseconds(10)));

        await Assert.That(block.Measure(60).MinLines).IsEqualTo(1 + 2 + 1);
        var collapsed = new ScreenBuffer(60, 4);
        block.Paint(new BlockPaintContext(collapsed, new Rect(0, 0, 60, 4), 0));
        string collapsedArt = GridDump.Art(collapsed);
        await Assert.That(collapsedArt).Contains("line-01");
        await Assert.That(collapsedArt).Contains("line-02");
        await Assert.That(collapsedArt).Contains("…");
        await Assert.That(collapsedArt).DoesNotContain("line-10");
        await Assert.That(collapsedArt).DoesNotContain("limit");

        block.ToggleExpanded();
        await Assert.That(block.Measure(60).MinLines).IsEqualTo(1 + 1 + 10);
        var expanded = new ScreenBuffer(60, 12);
        block.Paint(new BlockPaintContext(expanded, new Rect(0, 0, 60, 12), 0));
        string art = GridDump.Art(expanded);
        await Assert.That(art).Contains("read");
        await Assert.That(art).Contains("limit"); // full args row, not the 48-char summary
        await Assert.That(art).Contains("line-01");
        await Assert.That(art).Contains("line-10");
    }

    [Test]
    public async Task Expanded_Result_Is_Truncated_With_Overflow_Marker()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t9", "bash", "make"));
        string output = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line-{i:00}"));
        block.Complete(new ToolResultBody(output, isError: false, TimeSpan.FromMilliseconds(3)));
        block.SetExpanded(true);

        await Assert.That(block.Measure(60).MinLines)
            .IsEqualTo(1 + 1 + ToolCallBlock.ExpandedBodyLines + 1);
        var buffer = new ScreenBuffer(60, 1 + 1 + ToolCallBlock.ExpandedBodyLines + 1);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 60, buffer.Rows), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("line-20");
        await Assert.That(art).Contains("…");
        await Assert.That(art).DoesNotContain("line-21");
        await Assert.That(art).DoesNotContain("line-30");
    }

    [Test]
    public async Task TryHitHeader_Maps_Header_Row_Only()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "f.cs"));
        block.Complete(new ToolResultBody("out", isError: false, TimeSpan.FromMilliseconds(1)));

        await Assert.That(block.TryHitHeader(5, 3)).IsFalse(); // never painted

        var buffer = new ScreenBuffer(80, 8);
        block.Paint(new BlockPaintContext(buffer, new Rect(2, 3, 40, 2), 0));

        await Assert.That(block.TryHitHeader(5, 3)).IsTrue();
        await Assert.That(block.TryHitHeader(1, 3)).IsFalse(); // left of the card
        await Assert.That(block.TryHitHeader(50, 3)).IsFalse(); // right of the card
        await Assert.That(block.TryHitHeader(5, 4)).IsFalse(); // body row, not the header
    }

    [Test]
    public async Task Click_On_Header_Toggles_Via_Bridge()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", Args("{\"path\":\"a.cs\"}")));
            await bus.PublishAsync(new ToolExecutionEndEvent("tc1", ToolResult.Success("file body"), IsError: false));

            var tl = panel.Timeline;
            tl.PrepareFrame(40, 8);
            tl.Paint(new ScreenBuffer(40, 8), new Rect(0, 0, 40, 8)); // registers the header rect
            var card = (ToolCallBlock)tl.BlockAt(tl.Count - 1);

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Left, 5, 0, KeyModifiers.None))).IsTrue();
            await Assert.That(card.IsExpanded).IsTrue();

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Right, 5, 0, KeyModifiers.None))).IsFalse();

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Left, 5, 7, KeyModifiers.None))).IsFalse();
            await Assert.That(card.IsExpanded).IsTrue();
        }
    }

    [Test]
    public async Task Enter_Toggles_Newest_Card_Via_Bridge()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();

            await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", Args("{}")));
            await bus.PublishAsync(new ToolExecutionEndEvent("tc1", ToolResult.Success("one"), IsError: false));
            await bus.PublishAsync(new ToolExecutionStartEvent("tc2", "bash", Args("{}")));
            await bus.PublishAsync(new ToolExecutionEndEvent("tc2", ToolResult.Success("two"), IsError: false));

            var first = (ToolCallBlock)panel.Timeline.BlockAt(0);
            var second = (ToolCallBlock)panel.Timeline.BlockAt(1);

            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
            await Assert.That(second.IsExpanded).IsTrue();
            await Assert.That(first.IsExpanded).IsFalse();

            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Char(new Rune('x')))).IsFalse();

            await Assert.That(bridge.ToggleToolCard("missing")).IsFalse();
            await Assert.That(bridge.ToggleToolCard("tc1")).IsTrue();
            await Assert.That(first.IsExpanded).IsTrue();
        }
    }
}
