using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework;
using TUnit.Core;
using Harbor.TestKit;

namespace Harbor.Tui.CellForge.Tests;

// [UX5] #265 — subagent one-line card with live suffix (unanimous competitor
// pattern: opencode/crush/codex/kilocode). The task tool renders as ONE
// collapsed line: live suffix while running (current child tool / retry in
// red), "N toolcalls + duration" on finish; expand reveals the full child
// transcript; child text never streams into the parent feed.
[NotInParallel("pty")]
public class TaskCardTests
{
    private static (FakeEventBus Bus, ChatTimelinePanel Panel, StatusViewModel Status, ChatScreenBridge Bridge) Setup(
        string model = "m")
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = model };
        var bridge = new ChatScreenBridge(bus, panel, status);
        return (bus, panel, status, bridge);
    }

    private static async Task StartParentTaskAsync(FakeEventBus bus, ChatScreenBridge bridge, string taskId = "task1")
    {
        await bus.PublishAsync(TestEvents.AgentStart("s-parent", [WidgetFixtures.UserMsg("s-parent", "do recon")]));
        await bus.PublishAsync(new ToolExecutionStartEvent(
            taskId, "task", TestArgs.FromJson("{\"agent\":\"explore\",\"prompt\":\"find parsers\"}")));
        bridge.Tick(1000);
    }

    [Test]
    public async Task TaskCard_ChildToolsFeedLiveSuffix_NoOwnCards()
    {
        var (bus, panel, _, bridge) = Setup();
        using (bridge)
        {
            await StartParentTaskAsync(bus, bridge);

            // Live suffix tracks the running child tool…
            await bus.PublishAsync(new ToolExecutionStartEvent("c1", "read", TestArgs.FromJson("{\"path\":\"a.cs\"}")));
            bridge.Tick(1010);
            var card = (ToolCallBlock)panel.Timeline.BlockAt(panel.Timeline.Count - 1);
            await Assert.That(card.Info.ToolName).IsEqualTo("task");
            await Assert.That(card.LiveSuffix).IsNotNull();
            await Assert.That(card.LiveSuffix!.Contains("› read")).IsTrue();
            await Assert.That(card.LiveSuffixIsError).IsFalse();

            // …and the child never opens a card of its own.
            await Assert.That(panel.Timeline.Count).IsEqualTo(2); // user + task
            await bus.PublishAsync(TestEvents.ToolExecutionEnd("c1", ToolResult.Success("body")));
            await Assert.That(panel.Timeline.Count).IsEqualTo(2);
            await Assert.That(card.LiveSuffix).IsEqualTo("· 1 toolcall");
        }
    }

    [Test]
    public async Task TaskCard_Finish_ShowsToolcallCount_AndTranscriptOnExpand()
    {
        var (bus, panel, _, bridge) = Setup();
        using (bridge)
        {
            await StartParentTaskAsync(bus, bridge);

            await bus.PublishAsync(new ToolExecutionStartEvent("c1", "read", TestArgs.FromJson("{\"path\":\"a.cs\"}")));
            bridge.Tick(1010);
            await bus.PublishAsync(TestEvents.ToolExecutionEnd("c1", ToolResult.Success("body")));
            await bus.PublishAsync(new ToolExecutionStartEvent("c2", "grep", TestArgs.FromJson("{\"pattern\":\"parse\"}")));
            bridge.Tick(1120);
            await bus.PublishAsync(TestEvents.ToolExecutionEnd("c2", ToolResult.Success("hit")));
            bridge.Tick(1500);
            await bus.PublishAsync(TestEvents.ToolExecutionEnd("task1", ToolResult.Success("[sub-agent 'explore' finished — session s-child, 2 message(s)]\n\nfound it")));

            var card = (ToolCallBlock)panel.Timeline.BlockAt(panel.Timeline.Count - 1);
            await Assert.That(card.Status).IsEqualTo(ToolCallState.Success);
            await Assert.That(card.IsExpanded).IsFalse();
            await Assert.That(card.LiveSuffix).IsEqualTo("· 2 toolcalls");

            // ONE collapsed line (MaxBodyLines = 0 on task cards).
            await Assert.That(card.Measure(80).MinLines).IsEqualTo(1);

            // Duration still painted on the finished header.
            var buffer = new ScreenBuffer(80, 2);
            card.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 2), 0));
            string art = GridDump.Art(buffer);
            await Assert.That(art.Contains("✔ task")).IsTrue();
            await Assert.That(art.Contains("2 toolcalls")).IsTrue();

            // Expand reveals the full child transcript + final answer.
            card.SetExpanded(true);
            await Assert.That(card.Body.Value.Output.Contains("⚙ read")).IsTrue();
            await Assert.That(card.Body.Value.Output.Contains("⚙ grep")).IsTrue();
            await Assert.That(card.Body.Value.Output.Contains("found it")).IsTrue();
        }
    }

    [Test]
    public async Task TaskCard_ChildText_NeverStreamsIntoParent()
    {
        var (bus, panel, _, bridge) = Setup();
        using (bridge)
        {
            await StartParentTaskAsync(bus, bridge);

            // Child session announcement: transcript header only, no replay.
            await bus.PublishAsync(TestEvents.AgentStart("s-child", [WidgetFixtures.UserMsg("s-child", "secret")], kind: SessionKind.Subagent));
            await Assert.That(panel.Timeline.Count).IsEqualTo(2);

            // Child prose: suppressed from the feed…
            await bus.PublishAsync(TestEvents.MessageStart(WidgetFixtures.AssistantMsg("s-child", string.Empty)));
            await bus.PublishAsync(TestEvents.TextDeltaUpdate("d1", "secret child text", WidgetFixtures.AssistantMsg("s-child", "x")));
            await bus.PublishAsync(TestEvents.MessageEnd(WidgetFixtures.AssistantMsg("s-child", "secret child text")));
            await Assert.That(panel.Timeline.Count).IsEqualTo(2);
            await Assert.That(panel.Timeline.BlockAt(0).Kind).IsEqualTo("user");
            await Assert.That(panel.Timeline.BlockAt(1).Kind).IsEqualTo("tool-call");

            // …but buffered into the task transcript for expand.
            bridge.Tick(1200);
            await bus.PublishAsync(TestEvents.ToolExecutionEnd("task1", ToolResult.Success("final answer")));
            var card = (ToolCallBlock)panel.Timeline.BlockAt(panel.Timeline.Count - 1);
            await Assert.That(card.Body.Value.Output.Contains("secret child text")).IsTrue();
            await Assert.That(card.Body.Value.Output.Contains("final answer")).IsTrue();
        }
    }

    [Test]
    public async Task TaskCard_Retry_ShowsRedSuffix()
    {
        var (bus, panel, _, bridge) = Setup();
        using (bridge)
        {
            await StartParentTaskAsync(bus, bridge);

            await bus.PublishAsync(new ToolExecutionStartEvent("c1", "bash", TestArgs.FromJson("{\"cmd\":\"make\"}")));
            await bus.PublishAsync(TestEvents.ToolExecutionUpdate("c1", "retrying", 1, 3, 1.0));

            var card = (ToolCallBlock)panel.Timeline.BlockAt(panel.Timeline.Count - 1);
            await Assert.That(card.LiveSuffix).IsNotNull();
            await Assert.That(card.LiveSuffix!.Contains("1/3")).IsTrue();
            await Assert.That(card.LiveSuffixIsError).IsTrue();

            // Retry clears on child end; tally takes over.
            await bus.PublishAsync(TestEvents.ToolExecutionEnd("c1", ToolResult.Success("ok")));
            await Assert.That(card.LiveSuffixIsError).IsFalse();
            await Assert.That(card.LiveSuffix).IsEqualTo("· 1 toolcall");
        }
    }

    [Test]
    public async Task TaskCard_ChildAgentEnd_DoesNotIdleParent()
    {
        var (bus, panel, status, bridge) = Setup();
        using (bridge)
        {
            await StartParentTaskAsync(bus, bridge);
            await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Running);

            // Child run ends mid-task: parent footer stays Running, no mascot bounce.
            await bus.PublishAsync(TestEvents.AgentEnd([WidgetFixtures.AssistantMsg("s-child", "done")]));
            await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Running);
            await Assert.That(panel.Timeline.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task NonTask_ToolCalls_Unchanged_WithoutParentSession()
    {
        // Legacy path (no AgentStart seen): ordinary cards behave as before.
        var (bus, panel, _, bridge) = Setup();
        using (bridge)
        {
            await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", TestArgs.FromJson("{\"path\":\"src/a.cs\"}")));
            bridge.Tick(120);
            await bus.PublishAsync(TestEvents.ToolExecutionEnd(result: ToolResult.Success("file body")));

            var card = (ToolCallBlock)panel.Timeline.BlockAt(0);
            await Assert.That(card.Status).IsEqualTo(ToolCallState.Success);
            await Assert.That(card.LiveSuffix).IsNull();
            await Assert.That(card.MaxBodyLines).IsEqualTo(4);
        }
    }
}
