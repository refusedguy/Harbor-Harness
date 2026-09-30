using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Events;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

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
        await Assert.That(card.Status).IsEqualTo(ToolCallState.Success);
        await Assert.That(card.Body.Value.Output).IsEqualTo("file body");
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
        await Assert.That(card.Status).IsEqualTo(ToolCallState.Error);
        await Assert.That(card.Body.Value.Duration).IsEqualTo(TimeSpan.FromMilliseconds(50));
    }

    // #567: a cancelled run publishes AgentEndEvent(Cancelled: true) and never a
    // ToolExecutionEndEvent for the call it aborts. Before the collapsed
    // ToolCallState enum there was no state that meant "stopped", so the
    // in-flight card stayed Running and its ⚙ glyph spun forever. This pins the
    // bridge half; ToolCallBlockMapperTests pins the paint half.
    [Test]
    public async Task CancelledRun_StopsInFlightToolCard_InsteadOfSpinning()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new ToolExecutionStartEvent("tcX", "bash", System.Text.Json.JsonDocument.Parse("{\"cmd\":\"sleep 999\"}").RootElement.Clone()));
        bridge.Tick(40);

        var tl = panel.Timeline;
        var card = (ToolCallBlock)tl.BlockAt(tl.Count - 1);
        await Assert.That(card.Status).IsEqualTo(ToolCallState.Running);
        await Assert.That(card.Status.IsTerminal()).IsFalse();

        await bus.PublishAsync(new AgentEndEvent([], Cancelled: true));

        await Assert.That(card.Status).IsEqualTo(ToolCallState.Cancelled);
        await Assert.That(card.Status.IsTerminal()).IsTrue();
        await Assert.That(card.StatusPill).IsEqualTo("cancelled");
        await Assert.That(card.StatusPill).IsNotEqualTo("running");
        await Assert.That(card.StatusBrushKey).IsNotEqualTo("MochaYellow");
        await Assert.That(card.RawText()).DoesNotContain("⚙");
    }

    [Test]
    public async Task CancelledRun_DoesNotDisturbAlreadyCompletedCards()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        await bus.PublishAsync(new ToolExecutionStartEvent("tcDone", "read", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        bridge.Tick(10);
        await bus.PublishAsync(new ToolExecutionEndEvent("tcDone", ToolResult.Success("file body"), IsError: false));

        await bus.PublishAsync(new ToolExecutionStartEvent("tcLive", "bash", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        await bus.PublishAsync(new AgentEndEvent([], Cancelled: true));

        var tl = panel.Timeline;
        var done = (ToolCallBlock)tl.BlockAt(tl.Count - 2);
        var live = (ToolCallBlock)tl.BlockAt(tl.Count - 1);

        await Assert.That(done.Status).IsEqualTo(ToolCallState.Success);
        await Assert.That(done.StatusPill).IsEqualTo("ok");
        await Assert.That(live.Status).IsEqualTo(ToolCallState.Cancelled);
    }

    [Test]
    public async Task AgentError_RendersCollapsedCard_WithFullTextOnExpand()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        string blob = "API error 429: " + new string('x', 2000);
        await bus.PublishAsync(new AgentErrorEvent(blob));

        var tl = panel.Timeline;
        var card = (ToolCallBlock)tl.BlockAt(tl.Count - 1);
        await Assert.That(card.Status).IsEqualTo(ToolCallState.Error);
        await Assert.That(card.IsExpanded).IsFalse();
        await Assert.That(card.Body.Value.Output).IsEqualTo(blob);

        card.ToggleExpanded();
        await Assert.That(card.IsExpanded).IsTrue();
        await Assert.That(card.Body.Value.Output).IsEqualTo(blob);
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

        // The run republishes the full snapshot INCLUDING the echoed message.
        await bus.PublishAsync(new AgentStartEvent("s1", [
            UserMsg("s1", "hi"),
            AssistantMsg("s1", "hello!"),
        ]));

        var tl = panel.Timeline;
        await Assert.That(tl.Count).IsEqualTo(3); // echoed user + user from history + assistant
        await Assert.That(tl.BlockAt(0).RawText()).Contains("hi");
        await Assert.That(tl.BlockAt(1).RawText()).Contains("hi");
        await Assert.That(tl.BlockAt(2).Kind).IsEqualTo("assistant");
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
    public async Task ClickRouting_PendingGate_HintZones_ResolveAndDisarm()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        _ = bridge.BeginApprovalGate("bash", "rm -rf build/");
        var tl = panel.Timeline;
        tl.PrepareFrame(40, 8);
        tl.Paint(new ScreenBuffer(40, 8), new Rect(0, 0, 40, 8)); // registers LastPaintRect

        // Outside any hint zone → no consumption.
        await Assert.That(bridge.TryRouteApprovalClick(
            new Input.MouseEvent(MouseEventType.Press, MouseButton.Left, 1, 0, KeyModifiers.None))).IsFalse();

        // Left press on "[y]" approves; a right press never decides anything.
        await Assert.That(bridge.TryRouteApprovalClick(
            new Input.MouseEvent(MouseEventType.Press, MouseButton.Right, 1, 2, KeyModifiers.None))).IsFalse();
        await Assert.That(bridge.TryRouteApprovalClick(
            new Input.MouseEvent(MouseEventType.Press, MouseButton.Left, 15, 2, KeyModifiers.None))).IsTrue();
        await Assert.That(tl.Count).IsEqualTo(1);

        // After resolution, further clicks fall through (routing disarmed).
        await Assert.That(bridge.TryRouteApprovalClick(
            new Input.MouseEvent(MouseEventType.Press, MouseButton.Left, 27, 2, KeyModifiers.None))).IsFalse();
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
        var card = (Harbor.Tui.CellForge.Widgets.ImageBlock)panel.Timeline.BlockAt(1);
        await Assert.That(card.HasPngHeader).IsTrue();
        await Assert.That(card.Dimensions).IsEqualTo("640×480");
    }

    private static class ImageTestPng
    {
        internal static byte[] Header(uint w, uint h)
        {
            var d = new byte[24];
            d[0] = 0x89;
            d[1] = 0x50;
            d[2] = 0x4E;
            d[3] = 0x47;
            d[4] = 0x0D;
            d[5] = 0x0A;
            d[6] = 0x1A;
            d[7] = 0x0A;
            d[12] = (byte)'I';
            d[13] = (byte)'H';
            d[14] = (byte)'D';
            d[15] = (byte)'R';
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), h);
            return d;
        }
    }

    [Test]
    public async Task RequestApprovalGate_OffThread_LandsOnTimeline_OnlyOnTick()
    {
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

    [Test]
    public async Task TwoConsecutiveBashGates_ResolveInArrivalOrder_NoZombies()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        var first = bridge.BeginApprovalGate("bash", "rm -rf build/");
        var second = bridge.BeginApprovalGate("bash", "make clean");

        // Same-tool gates must not collide on router ids.
        await Assert.That(first.Id).IsNotEqualTo(second.Id);

        // Oldest gate is served first; deciding it exposes the next.
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
        await Assert.That(first.Decision).IsEqualTo(ApprovalChoice.Approve);
        await Assert.That(first.IsPending).IsFalse();
        await Assert.That(second.IsPending).IsTrue();

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('n')))).IsTrue();
        await Assert.That(second.Decision).IsEqualTo(ApprovalChoice.Deny);
        await Assert.That(second.IsPending).IsFalse();

        // No zombie pending blocks survive: routing fully disarmed.
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsFalse();
        await Assert.That(panel.Timeline.Count).IsEqualTo(2);
        for (int i = 0; i < panel.Timeline.Count; i++)
        {
            if (panel.Timeline.BlockAt(i) is ApprovalGateView gate)
            {
                await Assert.That(gate.IsPending).IsFalse();
            }
        }
    }

    [Test]
    public async Task RequestApprovalGate_TwoGates_RoutedInArrivalOrder_OnTick()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        var first = bridge.RequestApprovalGate("bash", "cargo build");
        var second = bridge.RequestApprovalGate("bash", "cargo test");

        bridge.Tick(100); // both queue entries land in one drain, arrival order
        await Assert.That(panel.Timeline.Count).IsEqualTo(2);
        await Assert.That(panel.Timeline.BlockAt(0)).IsSameReferenceAs(first);
        await Assert.That(panel.Timeline.BlockAt(1)).IsSameReferenceAs(second);

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
        await Assert.That(first.Decision).IsEqualTo(ApprovalChoice.Approve);
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(second.Decision).IsEqualTo(ApprovalChoice.AlwaysAllow);
        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsFalse();
    }

    [Test]
    public async Task ImageAttachment_QueuesInlinePayload_AlongsideTextCard()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc1",
            new ToolResult("saved", IsError: false, Attachments: [new FileAttachment("shots/ok.png", "image/png", [1, 2, 3, 4])]),
            IsError: false));

        var tl = panel.Timeline;
        await Assert.That(tl.BlockAt(tl.Count - 1).Kind).IsEqualTo("image");

        await Assert.That(bridge.TryTakePendingImage(out var image)).IsTrue();
        await Assert.That(image.Path).IsEqualTo("shots/ok.png");
        await Assert.That(image.MimeType).IsEqualTo("image/png");
        await Assert.That(image.Data.Length).IsEqualTo(4);
        await Assert.That(bridge.TryTakePendingImage(out _)).IsFalse();
    }

    [Test]
    public async Task NonImageAttachment_NoInlinePayloadQueued()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc1",
            new ToolResult("saved", IsError: false, Attachments: [new FileAttachment("report.pdf", "application/pdf", [1, 2, 3])]),
            IsError: false));

        await Assert.That(bridge.TryTakePendingImage(out _)).IsFalse();
    }

    [Test]
    public async Task ContextSegment_LightsUp_FromAgentStartWindow_AndStepFinish()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        // None-semantics: no window known yet → segment absent, not zero.
        await Assert.That(status.TryGetContextTokens(out _)).IsFalse();

        var model = new ModelInfo("hy3", "kilocode", "Kilocode Hy3", 10_000, 4096, false, false, true, Pricing.Unknown, "openai");
        await bus.PublishAsync(new AgentStartEvent("s1", [], model));

        // Window alone invents no usage — still dark until a request reports one.
        await Assert.That(status.TryGetContextTokens(out _)).IsFalse();

        // #623: the ctx segment is fed by the request the provider just accepted.
        // Turn 1 → per-request prompt tokens and session-cumulative spend coincide
        // (7400 in / 700 out), so the stats event below agrees with the step.
        await bus.PublishAsync(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(7400, 700)), AssistantMessage.Empty("s1", "m")));

        var metadata = new SessionMetadata(0.0123m, 7400, 700, 0, 0, 0, 2, null);
        await bus.PublishAsync(new SessionStatsEvent("s1", metadata));

        // Occupancy is the request's prompt tokens — output tokens of the step just
        // finished are not resident in the window until the next request carries them.
        await Assert.That(status.TryGetContextTokens(out var used)).IsTrue();
        await Assert.That(used).IsEqualTo(7400);
        await Assert.That(status.ContextWindow).IsEqualTo(10_000);

        // No model string set by the bridge → ctx bar is the first segment:
        // 74% → warn band, 4 of 6 cells (same pin as StatusSegmentBarTests).
        var ws = new StatusSeg[8];
        int n = status.BuildSegments(ws);
        await Assert.That(n).IsEqualTo(3); // ctx bar + tokens + cost
        await Assert.That(ws[0].Text).IsEqualTo("▰▰▰▰▱▱");
        await Assert.That(ws[0].Accent).IsEqualTo(StatusAccent.Warning);
    }

    [Test]
    public async Task ContextSegment_RequestBeforeWindow_LightsUp_WhenModelArrives()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        // A request that lands before the window is known is retained, not dropped.
        await bus.PublishAsync(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(1000, 200)), AssistantMessage.Empty("s1", "m")));

        var metadata = new SessionMetadata(0.001m, 1000, 200, 0, 0, 0, 1, null);
        await bus.PublishAsync(new SessionStatsEvent("s1", metadata));
        await Assert.That(status.TryGetContextTokens(out _)).IsFalse();

        var model = new ModelInfo("hy3", "kilocode", "Kilocode Hy3", 10_000, 4096, false, false, true, Pricing.Unknown, "openai");
        await bus.PublishAsync(new AgentStartEvent("s1", [], model));

        // The remembered request re-applies against the late window — no new
        // step finish needed.
        await Assert.That(status.TryGetContextTokens(out var used)).IsTrue();
        await Assert.That(used).IsEqualTo(1000);
    }

    [Test]
    public async Task ContextSegment_UnknownWindow_StaysDark()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        var model = new ModelInfo("m", "p", "M", 0, 4096, false, false, true, Pricing.Unknown, "openai");
        await bus.PublishAsync(new AgentStartEvent("s1", [], model));

        var metadata = new SessionMetadata(0.001m, 1000, 200, 0, 0, 0, 1, null);
        await bus.PublishAsync(new SessionStatsEvent("s1", metadata));

        await Assert.That(status.TryGetContextTokens(out _)).IsFalse();
        // Token/cost text still flows — only the ctx segment stays dark.
        await Assert.That(status.Tokens).IsEqualTo("1k↑ 200↓");
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

    [Test]
    public async Task ToolRetryUpdate_FeedsRetrySlot_AndEndClears_AndPaints()
    {
        // #76 regression: a scripted dispatcher retry produces a visible retry
        // projection through SetProjectedRetry; the settled call clears the slot.
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new ToolExecutionUpdateEvent(
            "tc1", "working…", RetryAttempt: 1, RetryMaxAttempts: 3, RetryBackoffSeconds: 0.2));
        await Assert.That(status.Retry).IsEqualTo("retry 1/3 in 1s");

        var composer = new ComposerController();
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        screen.Status.ProjectedState = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "running",
                Provider = "prov",
                Model = "m",
                AgentName = "code"
            }
        };
        screen.Status.SetProjectedRetry(1, 3, 1);

        var buffer = new ScreenBuffer(80, 8);
        screen.Tree.Solve(80, 8);
        foreach (var p in screen.Tree.Panels)
        {
            p.Paint(buffer);
        }

        await Assert.That(GridDump.Art(buffer)).Contains("retry 1/3 in 1s");

        // Ordinary progress updates (no retry fields) must not touch the slot.
        await bus.PublishAsync(new ToolExecutionUpdateEvent("tc1", "still working…"));
        await Assert.That(status.Retry).IsEqualTo("retry 1/3 in 1s");

        await bus.PublishAsync(new ToolExecutionEndEvent("tc1", ToolResult.Success("recovered"), IsError: false));
        await Assert.That(status.Retry).IsNull();
    }

    [Test]
    public async Task ManualPump_PublishesNothingUntilAcceptAsync()
    {
        // Issue #81: with autoSubscribe:false the bus publisher thread never
        // touches the timeline — the driven host pumps events through
        // AcceptAsync on the render thread instead.
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status, autoSubscribe: false);

        await bus.PublishAsync(new AgentStartEvent("s1", [UserMsg("s1", "hi there")]));
        await Assert.That(panel.Timeline.Count).IsEqualTo(0);

        await bridge.AcceptAsync(new AgentStartEvent("s1", [UserMsg("s1", "hi there")]));
        await Assert.That(panel.Timeline.Count).IsEqualTo(1);
        await Assert.That(panel.Timeline.BlockAt(0).Kind).IsEqualTo("user");
        await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Running);
    }

    [Test]
    public async Task SecondTurn_DoesNotRepaint_FirstAssistantAnswer()
    {
        // Prod dup-answer bug: the user sends a second message and the first
        // assistant answer renders twice. The streamed-then-committed message
        // was never recorded in the painted-id set, so the next AgentStart
        // replay re-appended it. Each message must paint exactly once.
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        const string sid = "s1";
        var now = DateTimeOffset.UtcNow;
        var u1 = new UserMessage("u1", sid, now, "first question", "code", "m");
        var a1 = new AssistantMessage(
            "a1", sid, now, [new TextPart("first answer")],
            StopReason.Stop, new Usage(0, 0), "m");
        var u2 = new UserMessage("u2", sid, now, "second question", "code", "m");
        var a2 = new AssistantMessage(
            "a2", sid, now, [new TextPart("second answer")],
            StopReason.Stop, new Usage(0, 0), "m");

        // ── Turn 1: user → assistant ──
        await bus.PublishAsync(new AgentStartEvent(sid, [u1]));
        await bus.PublishAsync(new MessageStartEvent(a1));
        await bus.PublishAsync(new MessageUpdateEvent(new TextDeltaEvent("t1", "first answer\n"), a1));
        await bus.PublishAsync(new MessageEndEvent(a1));
        await bus.PublishAsync(new TurnEndEvent(a1, [], sid));
        await bus.PublishAsync(new AgentEndEvent([u1, a1]));

        await Assert.That(panel.Timeline.Count).IsEqualTo(2); // u1 + committed a1

        // ── Turn 2: AgentStart replays the full history incl. settled a1 ──
        await bus.PublishAsync(new AgentStartEvent(sid, [u1, a1, u2]));
        await bus.PublishAsync(new MessageStartEvent(a2));
        await bus.PublishAsync(new MessageUpdateEvent(new TextDeltaEvent("t2", "second answer\n"), a2));
        await bus.PublishAsync(new MessageEndEvent(a2));
        await bus.PublishAsync(new TurnEndEvent(a2, [], sid));
        await bus.PublishAsync(new AgentEndEvent([u1, a1, u2, a2]));

        var tl = panel.Timeline;
        await Assert.That(tl.Count).IsEqualTo(4); // u1, a1, u2, a2 — no repaint

        int firstAnswerBlocks = 0;
        int secondAnswerBlocks = 0;
        for (int i = 0; i < tl.Count; i++)
        {
            string text = tl.BlockAt(i).RawText();
            if (text.Contains("first answer", StringComparison.Ordinal))
            {
                firstAnswerBlocks++;
            }

            if (text.Contains("second answer", StringComparison.Ordinal))
            {
                secondAnswerBlocks++;
            }
        }

        await Assert.That(firstAnswerBlocks).IsEqualTo(1);
        await Assert.That(secondAnswerBlocks).IsEqualTo(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    // #840 — the compaction lifecycle LINE. Distinct from #839's cell guard.
    //
    // #773/#839 asserted the status CELL on two projections (ChatAppReducer,
    // StatusBarViewModel). This asserts the TIMELINE, which is a different
    // object fed from a different place: `ProjectScreen` hands the store's
    // UiState to `Status.ProjectedState` and the sidebar only, so the
    // ChatRole.System line #839 added to `UiState.Chat.Lines` never reaches
    // `_panel.Timeline`. The bridge owns its own compaction output
    // ("compacting history…" / "history compacted") and owned none for the
    // failure — so on the canonical renderer the truncation was invisible even
    // after #839 landed. Adding this arm is NOT a second copy of #839's fix;
    // it is the only channel CellForge reads for these two siblings.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CompactionFailed_AppendsTheTruncationWarning_AndLeavesCompacting()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel { Model = "m" };
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new CompactionStartedEvent("s1"));
        await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Compacting);
        await Assert.That(panel.Timeline.BlockAt(0).RawText()).IsEqualTo("compacting history…");

        await bus.PublishAsync(new CompactionFailedEvent("s1", "summarizer timed out"));

        var tl = panel.Timeline;
        await Assert.That(tl.Count).IsEqualTo(2)
            .Because("the started arm and the failed arm are two blocks; a failure that "
                   + "reused or overwrote the started one would leave the transcript "
                   + "claiming the compaction was still running");

        await Assert.That(tl.BlockAt(1).Kind).IsEqualTo("system");
        await Assert.That(tl.BlockAt(1).RawText())
            .IsEqualTo("compaction failed: summarizer timed out — continuing on truncated history")
            .Because("this is #839's ChatAppReducer.OnCompactionFailed sentence, asserted as "
                   + "a literal: the same degradation, said the same way on every "
                   + "surface, is the whole point of the fix. CompactionBehavior names "
                   + "the fallback irreversible and then lets the run continue — nothing "
                   + "downstream of the event tells the user their context was cut");

        await Assert.That(status.Mode).IsEqualTo(StatusBarMode.Running)
            .Because("this is the bridge's OWN StatusViewModel, not the store projection "
                   + "that ChatScreenLayout:585 prefers. #839's report called the two "
                   + "out of step underneath the projection; the fallback path that "
                   + "reads this one has to agree with its siblings or the bar reads "
                   + "\"compacting\" for a run that is demonstrably running");
    }

    [Test]
    public async Task CompactionFailed_DoesNotReuseTheSuccessMarker()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        using var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());

        await bus.PublishAsync(new CompactionFailedEvent("s1", "summarizer timed out"));

        string text = panel.Timeline.BlockAt(0).RawText();
        await Assert.That(text).DoesNotContain("history compacted")
            .Because("nothing was compacted. Reusing the success sibling's wording would "
                   + "tell the reader their history was pruned when the summarizer "
                   + "failed and the fallback, not the compaction, is what cut it");
    }
}
