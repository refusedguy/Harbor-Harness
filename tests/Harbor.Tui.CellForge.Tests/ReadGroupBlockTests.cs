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

// [UX3] #263 — consecutive read-only context tools (read/glob/grep/ls/tree/
// ripgrep) collapse into one "gathered context" group line (opencode pattern),
// expandable to one row per member card. Coalescing lives in ToolCardTracker
// (card-creation path); block rendering of ToolCallBlock is untouched.
[NotInParallel("pty")]
public class ReadGroupBlockTests
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

    private static async Task ExecAsync(FakeEventBus bus, ChatScreenBridge bridge, string id, string tool, string args, long tickMs)
    {
        await bus.PublishAsync(new ToolExecutionStartEvent(id, tool, Args(args)));
        bridge.Tick(tickMs);
        await bus.PublishAsync(new ToolExecutionEndEvent(id, ToolResult.Success("ok-" + id), IsError: false));
    }

    [Test]
    public async Task Single_Read_Stays_Plain_Card()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 120);

            await Assert.That(panel.Timeline.Count).IsEqualTo(1);
            await Assert.That(panel.Timeline.BlockAt(0).Kind).IsEqualTo("tool-call");
            var card = (ToolCallBlock)panel.Timeline.BlockAt(0);
            await Assert.That(card.Info.ToolName).IsEqualTo("read");
        }
    }

    [Test]
    public async Task Two_Consecutive_Reads_Collapse_Into_Group()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 100);
            await Assert.That(panel.Timeline.Count).IsEqualTo(1); // still single

            await ExecAsync(bus, bridge, "tc2", "read", "{\"path\":\"b.cs\"}", 200);
            await Assert.That(panel.Timeline.Count).IsEqualTo(1); // pair -> group

            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);
            await Assert.That(group.Kind).IsEqualTo("read-group");
            await Assert.That(group.Members.Count).IsEqualTo(2);
            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.HeaderText()).Contains("gathered context");
            await Assert.That(group.HeaderText()).Contains("2 read");
            await Assert.That(group.Measure(80).MinLines).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Mixed_Read_And_Search_Counts()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 10);
            await ExecAsync(bus, bridge, "tc2", "glob", "{\"pattern\":\"**/*.cs\"}", 20);
            await ExecAsync(bus, bridge, "tc3", "grep", "{\"pattern\":\"parse\"}", 30);

            await Assert.That(panel.Timeline.Count).IsEqualTo(1);
            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);
            await Assert.That(group.Members.Count).IsEqualTo(3);
            await Assert.That(group.HeaderText()).Contains("1 read");
            await Assert.That(group.HeaderText()).Contains("2 search");
        }
    }

    [Test]
    public async Task Search_Only_Group_Has_No_Read_Part()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "ls", "{\"path\":\".\"}", 10);
            await ExecAsync(bus, bridge, "tc2", "tree", "{\"path\":\".\"}", 20);
            await ExecAsync(bus, bridge, "tc3", "ripgrep", "{\"pattern\":\"x\"}", 30);

            await Assert.That(panel.Timeline.Count).IsEqualTo(1);
            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);
            await Assert.That(group.HeaderText()).Contains("3 search");
            await Assert.That(group.HeaderText().Contains("read")).IsFalse();
        }
    }

    [Test]
    public async Task Running_Members_Group_Live_Before_Completion()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", Args("{\"path\":\"a.cs\"}")));
            bridge.Tick(100);
            await bus.PublishAsync(new ToolExecutionStartEvent("tc2", "read", Args("{\"path\":\"b.cs\"}")));
            bridge.Tick(110);

            await Assert.That(panel.Timeline.Count).IsEqualTo(1);
            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);
            await Assert.That(group.Members.Count).IsEqualTo(2);

            await bus.PublishAsync(new ToolExecutionEndEvent("tc1", ToolResult.Success("a"), IsError: false));
            await bus.PublishAsync(new ToolExecutionEndEvent("tc2", ToolResult.Success("b"), IsError: false));
            await Assert.That(panel.Timeline.Count).IsEqualTo(1);
            await Assert.That(group.Members.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Non_Readonly_Breaks_Run()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 10);
            await ExecAsync(bus, bridge, "tc2", "read", "{\"path\":\"b.cs\"}", 20);
            await ExecAsync(bus, bridge, "tc3", "bash", "{\"cmd\":\"make\"}", 30);
            await ExecAsync(bus, bridge, "tc4", "read", "{\"path\":\"c.cs\"}", 40);

            await Assert.That(panel.Timeline.Count).IsEqualTo(3);
            await Assert.That(panel.Timeline.BlockAt(0).Kind).IsEqualTo("read-group");
            await Assert.That(panel.Timeline.BlockAt(1).Kind).IsEqualTo("tool-call");
            await Assert.That(((ToolCallBlock)panel.Timeline.BlockAt(1)).Info.ToolName).IsEqualTo("bash");
            await Assert.That(panel.Timeline.BlockAt(2).Kind).IsEqualTo("tool-call");
        }
    }

    [Test]
    public async Task Toggle_By_Member_Or_Group_Id_Expands_Group()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 10);
            await ExecAsync(bus, bridge, "tc2", "grep", "{\"pattern\":\"x\"}", 20);

            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);

            await Assert.That(bridge.ToggleToolCard("missing")).IsFalse();
            await Assert.That(bridge.ToggleToolCard("tc1")).IsTrue();
            await Assert.That(group.IsExpanded).IsTrue();
            await Assert.That(group.Measure(80).MinLines).IsEqualTo(1 + 2);

            await Assert.That(bridge.ToggleToolCard(group.Id)).IsTrue();
            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.Measure(80).MinLines).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Enter_Toggles_Group_Via_Bridge()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 10);
            await ExecAsync(bus, bridge, "tc2", "read", "{\"path\":\"b.cs\"}", 20);

            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);
            await Assert.That(bridge.TryRouteToolCardKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
            await Assert.That(group.IsExpanded).IsTrue();
        }
    }

    [Test]
    public async Task Collapsed_Paint_Shows_Header_Expanded_Shows_Member_Rows()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", "{\"path\":\"a.cs\"}", 10);
            await ExecAsync(bus, bridge, "tc2", "grep", "{\"pattern\":\"parse\"}", 20);

            var group = (ReadGroupBlock)panel.Timeline.BlockAt(0);

            var collapsed = new ScreenBuffer(80, 1);
            group.Paint(new BlockPaintContext(collapsed, new Rect(0, 0, 80, 1), 0));
            string collapsedArt = GridDump.Art(collapsed);
            await Assert.That(collapsedArt).Contains("gathered context");
            await Assert.That(collapsedArt).Contains("1 read");
            await Assert.That(collapsedArt).Contains("1 search");

            await Assert.That(bridge.ToggleToolCard("tc2")).IsTrue();
            var expanded = new ScreenBuffer(80, 3);
            group.Paint(new BlockPaintContext(expanded, new Rect(0, 0, 80, 3), 0));
            string art = GridDump.Art(expanded);
            await Assert.That(art).Contains("read");
            await Assert.That(art).Contains("a.cs");
            await Assert.That(art).Contains("grep");
        }
    }

    [Test]
    public async Task Click_On_Group_Header_Toggles_Via_Bridge()
    {
        var (bus, panel, bridge) = Setup();
        using (bridge)
        {
            await ExecAsync(bus, bridge, "tc1", "read", Args("{\"path\":\"a.cs\"}"), 10);
            await ExecAsync(bus, bridge, "tc2", "read", Args("{\"path\":\"b.cs\"}"), 20);

            var tl = panel.Timeline;
            tl.PrepareFrame(40, 8);
            tl.Paint(new ScreenBuffer(40, 8), new Rect(0, 0, 40, 8)); // registers the header rect
            var group = (ReadGroupBlock)tl.BlockAt(tl.Count - 1);

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Left, 5, 0, KeyModifiers.None))).IsTrue();
            await Assert.That(group.IsExpanded).IsTrue();

            await Assert.That(bridge.TryRouteToolCardClick(
                new Input.MouseEvent(Input.MouseEventType.Press, Input.MouseButton.Right, 5, 0, KeyModifiers.None))).IsFalse();
            await Assert.That(group.IsExpanded).IsTrue();
        }
    }
}
