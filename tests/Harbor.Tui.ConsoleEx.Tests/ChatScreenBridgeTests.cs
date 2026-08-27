using System.Text;
using Harbor.Tui.ConsoleEx.Input;
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

    // ── CE-4 З.2: живой REPL ──────────────────────────────────────────────

    [Test]
    public async Task LocallyEchoedPrompt_IsNotDuplicated_ByNextReplay()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        // REPL echoed the submitted prompt before PromptAsync ran.
        panel.Timeline.Append(new UserBlock("hi"));
        bridge.NotifyLocalUserMessage();

        // The run republishes the full snapshot INCLUDING the echoed message.
        await bus.PublishAsync(new AgentStartEvent("s1", [
            UserMsg("s1", "hi"),
            AssistantMsg("s1", "hello!"),
        ]));

        var tl = panel.Timeline;
        await Assert.That(tl.Count).IsEqualTo(2); // echoed user + assistant — no duplicate "hi"
        await Assert.That(tl.BlockAt(0).RawText()).Contains("hi");
        await Assert.That(tl.BlockAt(1).Kind).IsEqualTo("assistant");
    }

    [Test]
    public async Task RepeatedAgentStart_RepublishingSameHistory_DoesNotDuplicate()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        var history = new AgentMessage[] { UserMsg("s1", "q1"), AssistantMsg("s1", "a1") };
        await bus.PublishAsync(new AgentStartEvent("s1", history));
        await bus.PublishAsync(new AgentStartEvent("s1", history));
        await bus.PublishAsync(new AgentStartEvent("s1", history));

        await Assert.That(panel.Timeline.Count).IsEqualTo(2); // user + assistant, once each
    }

    [Test]
    public async Task AgentStart_SetsStatusRunning_AgentEnd_Idle()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new AgentStartEvent("s1", []));
        await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Running);

        await bus.PublishAsync(new AgentEndEvent([]));
        await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Idle);
    }

    [Test]
    public async Task SessionStats_Feed_StatusUsage()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status);

        var metadata = new Harbor.Abstractions.Models.SessionMetadata(
            Cost: 0.0123m, TokensInput: 1500, TokensOutput: 300,
            TokensReasoning: 0, TokensCacheRead: 0, TokensCacheWrite: 0,
            MessageCount: 2, TimeCompacting: null);
        await bus.PublishAsync(new SessionStatsEvent("s1", metadata));

        await Assert.That(status.Tokens).IsEqualTo("1.5k↑ 300↓");
        await Assert.That(status.Cost).IsEqualTo("$0.0123");
    }

    [Test]
    public async Task BeginApprovalGate_AppendsBlock_AndRoutesKey()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        var gate = bridge.BeginApprovalGate("bash", "rm -rf build/");
        var tl = panel.Timeline;
        await Assert.That(tl.BlockAt(tl.Count - 1)).IsSameReferenceAs(gate);

        // Unrelated key falls through; y is consumed and resolves the gate.
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('q')))).IsFalse();
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.Approve);
        await Assert.That(gate.IsPending).IsFalse();

        // Gate resolved → routing disarms; further keys go back to the composer path.
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('n')))).IsFalse();
    }

    [Test]
    public async Task HistoryReplay_ImageFilePart_BecomesImageCard()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 40, 6);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        var image = new AssistantMessage(
            Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow,
            [
                new TextPart("before"),
                new FilePart("shots/a.png", "image/png", 2048, ImageTestPng.Header(640, 480)),
            ], StopReason.Stop, new Usage(0, 0), "test-model");

        await bus.PublishAsync(new AgentStartEvent("s1", [image]));

        await Assert.That(panel.Timeline.Count).IsEqualTo(2); // markdown + image card
        var card = (Harbor.Tui.ConsoleEx.Widgets.ImageBlock)panel.Timeline.BlockAt(1);
        await Assert.That(card.HasPngHeader).IsTrue();
        await Assert.That(card.Dimensions).IsEqualTo("640×480");
    }

    private static class ImageTestPng
    {
        internal static byte[] Header(uint w, uint h)
        {
            var d = new byte[24];
            d[0] = 0x89; d[1] = 0x50; d[2] = 0x4E; d[3] = 0x47;
            d[4] = 0x0D; d[5] = 0x0A; d[6] = 0x1A; d[7] = 0x0A;
            d[12] = (byte)'I'; d[13] = (byte)'H'; d[14] = (byte)'D'; d[15] = (byte)'R';
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), h);
            return d;
        }
    }

    [Test]
    public async Task RequestApprovalGate_OffThread_LandsOnTimeline_OnlyOnTick()    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        var gate = bridge.RequestApprovalGate("bash", "cargo build");
        await Assert.That(panel.Timeline.Count).IsEqualTo(0); // not mutated off the render thread

        bridge.Tick(100);
        await Assert.That(panel.Timeline.BlockAt(panel.Timeline.Count - 1)).IsSameReferenceAs(gate);

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.AlwaysAllow);
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
