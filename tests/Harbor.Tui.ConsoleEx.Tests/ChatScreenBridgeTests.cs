using Harbor.Abstractions.Models;
using Harbor.Abstractions.Events;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class ChatScreenBridgeTests
{
    private static AssistantMessage AssistantMsg(string sessionId, params string[] texts) => new(
        Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow,
        [.. texts.Select(t => new TextPart(t))], StopReason.Stop, new Usage(0, 0), "test-model");

    private static UserMessage UserMsg(string sessionId, string content) => new(
        Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow,
        content, "code", "test-model");

    [Test]
    public async Task FullTurn_Flows_IntoBlocks()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status);

        // History replay on AgentStart.
        await bus.PublishAsync(new AgentStartEvent("s1", [UserMsg("s1", "hi there"), AssistantMsg("s1", "hello!")]));
        await bus.PublishAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));

        long t0 = 100;
        bridge.Tick(t0);
        await bus.PublishAsync(new MessageUpdateEvent(new TextDeltaEvent("id", "Answer **line**\n"), AssistantMessage.Empty("s1", "m")));
        await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", System.Text.Json.JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement.Clone()));
        await bus.PublishAsync(new ToolExecutionEndEvent("tc1", ToolResult.Success("file body"), IsError: false));
        await bus.PublishAsync(new MessageEndEvent(AssistantMsg("s1", "Answer line")));
        await bus.PublishAsync(new AgentEndEvent([]));

        var tl = panel.Timeline;
        await Assert.That(tl.Count).IsEqualTo(4); // user, assistant(history), stream-slot→markdown, toolcard

        var kinds = Enumerable.Range(0, tl.Count).Select(i => tl.BlockAt(i).Kind).ToArray();
        await Assert.That(kinds[0]).IsEqualTo("user");
        await Assert.That(kinds[1]).IsEqualTo("assistant");
        await Assert.That(kinds[2]).IsEqualTo("assistant"); // committed stream slot
        await Assert.That(kinds[3]).IsEqualTo("tool-call");

        var card = (ToolCallBlock)tl.BlockAt(3);
        await Assert.That(card.Status).IsEqualTo(ToolCallStatus.Ok);
        await Assert.That(card.Body!.Output).IsEqualTo("file body");
        await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Idle);
    }

    [Test]
    public async Task Pacer_GatesReveal_OneLinePerTick_InSmoothMode()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new MessageStartEvent(AssistantMessage.Empty("s", "m")));

        // Five complete source lines arrive at once.
        await bus.PublishAsync(new MessageUpdateEvent(new TextDeltaEvent("i", "l1\nl2\nl3\nl4\nl5\n"), AssistantMessage.Empty("s", "m")));

        bridge.Tick(nowMs: 0);
        int afterTick1 = VisibleChars(panel);

        bridge.Tick(nowMs: 16);
        int afterTick2 = VisibleChars(panel);

        // Smooth mode: exactly one queued line per tick.
        await Assert.That(afterTick2 - afterTick1).IsEqualTo(3); // "l2\n"
        await Assert.That(afterTick2).IsGreaterThan(afterTick1);
    }

    [Test]
    public async Task Burst_TriggersCatchUp_AndDrainsAll()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new MessageStartEvent(AssistantMessage.Empty("s", "m")));

        // ≥ EnterDepth lines → CatchUp pressure.
        var burst = string.Join("", Enumerable.Range(1, CommitTickPacer.EnterDepth + 2).Select(i => $"line{i}\n"));
        await bus.PublishAsync(new MessageUpdateEvent(new TextDeltaEvent("i", burst), AssistantMessage.Empty("s", "m")));

        // Transition tick enters CatchUp but still reveals a single line…
        bridge.Tick(nowMs: 200);
        _ = panel.Timeline.PrepareFrame(40, 200);
        await Assert.That(panel.Timeline.TotalHeight).IsGreaterThanOrEqualTo(1);

        // …the next tick drains everything queued (BatchAll).
        bridge.Tick(nowMs: 201);
        _ = panel.Timeline.PrepareFrame(40, 200);
        await Assert.That(panel.Timeline.TotalHeight).IsGreaterThanOrEqualTo(CommitTickPacer.EnterDepth + 1);
    }

    [Test]
    public async Task DiffExtraction_FromMetadata_AndFromOutput()
    {
        const string diff = "--- a/f\n+++ b/f\n@@ -1,1 +1,2 @@\n-old\n+new";
        var viaMeta = ChatScreenBridge.TryExtractDiff(ToolResult.Success("ok", metadata: diff));
        var viaOutput = ChatScreenBridge.TryExtractDiff(ToolResult.Success(diff));
        var none = ChatScreenBridge.TryExtractDiff(ToolResult.Success("plain text output"));

        await Assert.That(viaMeta).IsEqualTo(diff);
        await Assert.That(viaOutput).IsEqualTo(diff);
        await Assert.That(none).IsNull();
    }

    [Test]
    public async Task ErrorToolCard_ShowsErrorState()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new ToolExecutionStartEvent("tc9", "bash", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        bridge.Tick(50);
        await bus.PublishAsync(new ToolExecutionEndEvent("tc9", ToolResult.Error("exit code 1"), IsError: true));

        var tl = panel.Timeline;
        var card = (ToolCallBlock)tl.BlockAt(tl.Count - 1);
        await Assert.That(card.Status).IsEqualTo(ToolCallStatus.Error);
        await Assert.That(card.Body!.Duration).IsEqualTo(TimeSpan.FromMilliseconds(50));
    }

    private static int VisibleChars(ChatTimelinePanel panel)
    {
        int total = 0;
        for (int i = 0; i < panel.Timeline.Count; i++)
        {
            total += panel.Timeline.BlockAt(i).RawText().Length;
        }

        return total;
    }
}
