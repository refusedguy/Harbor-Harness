using System.Collections.Immutable;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions.State;
using Harbor.Tui.SpectreTui;
using Harbor.Tui.SpectreTui.View;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for the TEA core (UiReducer / UiStore / InputModel) — the shared,
///     renderer-agnostic state machine. No terminal, no IAgent, no Spectre
///     widgets: this is the pure logic that every interactive renderer projects
///     from.
/// </summary>
public class UiReducerTests
{
    [Test]
    public async Task AgentStart_SeedsUserLines_OnlyWhenEmpty()
    {
        var state = new UiState();
        var ev = new AgentStartEvent("s1", new AgentMessage[]
        {
            new UserMessage("u1", "s1", default, "hi", "code", "m1")
        });

        var next = UiReducer.Reduce(state, ev);

        await Assert.That(next.Status).IsEqualTo("running");
        await Assert.That(next.IsAgentRunning).IsTrue();
        await Assert.That(next.Lines.Length).IsEqualTo(1);
        await Assert.That(next.Lines[0].Role).IsEqualTo(ChatRole.User);
        await Assert.That(next.Lines[0].Text).IsEqualTo("hi");
    }

    [Test]
    public async Task TextDeltas_Accumulate_ThenFlushOnMessageEnd()
    {
        var s = new UiState();
        s = UiReducer.Reduce(s, new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        s = UiReducer.Reduce(s, new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
        s = UiReducer.Reduce(s, new MessageUpdateEvent(new TextDeltaEvent("0", " world"), AssistantMessage.Empty("s1", "m")));
        await Assert.That(s.Active.TextBuffer).IsEqualTo("Hello world");
        await Assert.That(s.IsStreaming).IsTrue();

        s = UiReducer.Reduce(s, new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
        await Assert.That(s.Lines.Length).IsEqualTo(1);
        await Assert.That(s.Lines[0].Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(s.Lines[0].Text).IsEqualTo("Hello world");
        await Assert.That(s.IsStreaming).IsFalse();
        await Assert.That(s.Active.TextBuffer).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ThinkingDelta_FlushesToThinkingLine()
    {
        var s = new UiState();
        s = UiReducer.Reduce(s, new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        s = UiReducer.Reduce(s, new MessageUpdateEvent(new ThinkingDeltaEvent("0", "hmm"), AssistantMessage.Empty("s1", "m")));
        s = UiReducer.Reduce(s, new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
        await Assert.That(s.Lines.Length).IsEqualTo(1);
        await Assert.That(s.Lines[0].Role).IsEqualTo(ChatRole.Thinking);
        await Assert.That(s.Lines[0].Text).IsEqualTo("hmm");
    }

    [Test]
    public async Task StepFinish_AccumulatesCost()
    {
        var s = new UiState();
        s = UiReducer.Reduce(s, new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(1_000_000, 2_000_000)),
            AssistantMessage.Empty("s1", "m")));

        await Assert.That(s.Cost.TokensIn).IsEqualTo(1_000_000);
        await Assert.That(s.Cost.TokensOut).IsEqualTo(2_000_000);
        await Assert.That(s.Cost.CostUsd).IsEqualTo(3m + 30m);
    }

    [Test]
    public async Task AgentEnd_ResetsRunningState()
    {
        var s = new UiState { IsAgentRunning = true, IsStreaming = true, Status = "running" };
        var next = UiReducer.Reduce(s, new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(next.IsAgentRunning).IsFalse();
        await Assert.That(next.IsStreaming).IsFalse();
        await Assert.That(next.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task ToolEvents_AppendToolLines()
    {
        var s = new UiState();
        s = UiReducer.Reduce(s, new ToolExecutionStartEvent("tc1", "read", JsonDocument.Parse("{}").RootElement));
        s = UiReducer.Reduce(s, new ToolExecutionEndEvent("tc1", new ToolResult("ok", false), false));
        await Assert.That(s.Lines.Length).IsEqualTo(2);
        await Assert.That(s.Lines[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(s.Lines[1].Role).IsEqualTo(ChatRole.ToolResult);
    }
}

public class InputModelTests
{
    [Test]
    public async Task Initially_Empty()
    {
        var input = InputModel.Empty;
        await Assert.That(input.IsEmpty).IsTrue();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Append_BuildsText()
    {
        var input = InputMsg.Update(InputModel.Empty, new InputMsg.Char('h'));
        input = InputMsg.Update(input, new InputMsg.Char('i'));
        await Assert.That(input.Text).IsEqualTo("hi");
    }

    [Test]
    public async Task Backspace_RemovesLastChar()
    {
        var input = InputMsg.Update(InputModel.Empty, new InputMsg.Char('a'));
        input = InputMsg.Update(input, new InputMsg.Char('b'));
        input = InputMsg.Update(input, new InputMsg.Backspace());
        await Assert.That(input.Text).IsEqualTo("a");
    }

    [Test]
    public async Task Consume_AddsToHistory()
    {
        var input = InputMsg.Update(InputModel.Empty, new InputMsg.Char('h'));
        input = InputMsg.Update(input, new InputMsg.Char('i'));
        var (next, submitted) = input.Consume();
        await Assert.That(submitted).IsEqualTo("hi");
        await Assert.That(next.Text).IsEqualTo(string.Empty);
        await Assert.That(next.History.Length).IsEqualTo(1);
    }

    [Test]
    public async Task HistoryNavigates_UpThenDown()
    {
        var input = InputMsg.Update(InputModel.Empty, new InputMsg.Char('a'));
        input = InputMsg.Update(input, new InputMsg.Char('b'));
        var (next, _) = input.Consume();
        next = InputMsg.Update(next, new InputMsg.Char('c'));
        next = InputMsg.Update(next, new InputMsg.Char('d'));
        (next, _) = next.Consume();

        next = InputMsg.Update(next, new InputMsg.HistoryUp());
        await Assert.That(next.Text).IsEqualTo("cd");
        next = InputMsg.Update(next, new InputMsg.HistoryUp());
        await Assert.That(next.Text).IsEqualTo("ab");
        next = InputMsg.Update(next, new InputMsg.HistoryDown());
        await Assert.That(next.Text).IsEqualTo("cd");
        next = InputMsg.Update(next, new InputMsg.HistoryDown());
        await Assert.That(next.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Autocomplete_Slashes()
    {
        var slash = ImmutableArray.Create("/help", "/exit", "/setup");
        var input = InputMsg.Update(InputModel.Empty, new InputMsg.Char('/'));
        input = InputMsg.Update(input, new InputMsg.Char('h'));
        input = InputMsg.Update(input, new InputMsg.Autocomplete(slash));
        await Assert.That(input.Text).IsEqualTo("/help ");
    }
}

public class UiStoreTests
{
    [Test]
    public async Task Dispatch_RaisesChanged_WithNewState()
    {
        var store = new UiStore();
        UiState? seen = null;
        store.Changed += (_, e) => seen = e.State;

        store.Dispatch(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Status).IsEqualTo("running");
        await Assert.That(store.State.Status).IsEqualTo("running");
    }

    [Test]
    public async Task BindSession_SetsChrome()
    {
        var store = new UiStore();
        store.BindSession("m", "p", "agent");
        await Assert.That(store.State.Model).IsEqualTo("m");
        await Assert.That(store.State.Provider).IsEqualTo("p");
        await Assert.That(store.State.AgentName).IsEqualTo("agent");
    }

    [Test]
    public async Task Reset_ClearsLines()
    {
        var store = new UiStore();
        store.Dispatch(new AgentStartEvent("s1",
            new AgentMessage[] { new UserMessage("u1", "s1", default, "hi", "code", "m") }));
        await Assert.That(store.State.Lines.Length).IsEqualTo(1);
        store.Reset();
        await Assert.That(store.State.Lines.Length).IsEqualTo(0);
    }
}

/// <summary>Smoke tests that the SpectreTui renderer constructs and renders events without throwing.</summary>
public class SpectreTuiRendererTests
{
    private static SpectreTuiRenderer CreateRenderer() => new(NullLogger<SpectreTuiRenderer>.Instance);

    [Test]
    public async Task Constructor_SetsContext()
    {
        var renderer = CreateRenderer();
        await Assert.That(renderer.Context).IsNotNull();
        await Assert.That(renderer.Context).IsTypeOf<SpectreTuiRenderContext>();
        renderer.Dispose();
    }

    [Test]
    public async Task InitializeAsync_ReturnsSuccess()
    {
        var renderer = CreateRenderer();
        var result = await renderer.InitializeAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithAllEventTypes_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();

        await renderer.RenderAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await renderer.RenderAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageUpdateEvent(new ThinkingDeltaEvent("0", "Thinking..."), AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageUpdateEvent(new ToolCallStartEvent("tc1", "read"), AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new ToolExecutionStartEvent("tc1", "read", JsonDocument.Parse("{}").RootElement));
        await renderer.RenderAsync(new ToolExecutionEndEvent("tc1", new ToolResult("output", false), false));
        await renderer.RenderAsync(new AgentErrorEvent("boom"));
        await renderer.RenderAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await renderer.RenderAsync(new CompactionStartedEvent("s1"));
        await renderer.RenderAsync(new CompactionCompletedEvent("s1", "summary", 10, 1000, TimeSpan.FromSeconds(1)));

        await Assert.That(renderer.Context.SupportsColor).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        var renderer = CreateRenderer();
        renderer.Dispose();
        renderer.Dispose();
        await Assert.That(true).IsTrue();
    }
}

public class SpectreTuiChatViewProjectorTests
{
    [Test]
    public async Task BuildWidgets_WithUnbalancedBrackets_DoesNotThrow()
    {
        // Regression: agent output containing '[' or ']' (code, JSON, tables) must
        // not be parsed as markup and must not throw "Unbalanced markup stack".
        var lines = ImmutableArray.Create(
            new ChatLine(ChatRole.Assistant, "arr[i] = x[0]; if (a]b) {}"),
            new ChatLine(ChatRole.Tool, "→ read  {\"path\":\"a\"}"),
            new ChatLine(ChatRole.ToolResult, "✓ [1, 2, 3] ok ] trailing"),
            new ChatLine(ChatRole.User, "use a[b] syntax"));
        var layout = new ChatViewProjector();
        layout.SetLines(lines, isStreaming: false, ActiveMessage.Empty);

        var widgets = layout.BuildWidgets(0);

        await Assert.That(widgets.Count).IsEqualTo(6);
        await Assert.That(widgets.ContainsKey("History")).IsTrue();
    }

    [Test]
    public async Task BuildWidgets_StreamingAppendsActiveBuffers()
    {
        var lines = ImmutableArray.Create(new ChatLine(ChatRole.User, "hi"));
        var layout = new ChatViewProjector();
        layout.SetLines(lines, isStreaming: true, new ActiveMessage("Hello", "Thinking…"));

        var widgets = layout.BuildWidgets(0);

        await Assert.That(widgets.Count).IsEqualTo(6);
        await Assert.That(layout.IsStreaming).IsTrue();
    }

    [Test]
    public async Task BuildHistory_SlicesInDisplayRows_NotChatLines()
    {
        // Regression for "scroll mixes ChatLine count with terminal rows": a single
        // long message that expands to many display rows must be sliced by ROW count,
        // so a small viewport never surfaces more rows than it can show, and a large
        // viewport with few long messages must not report a dead (zero) scroll.
        var longMsg =
            "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
        var lines = ImmutableArray.Create(
            new ChatLine(ChatRole.User, "u1"),
            new ChatLine(ChatRole.Assistant, longMsg),
            new ChatLine(ChatRole.User, "u2"));

        var layout = new ChatViewProjector();
        layout.SetLines(lines, isStreaming: false, ActiveMessage.Empty);

        // 3 messages → (1+1) + (10+1) + (1+1) = 15 display rows total.
        const int viewport = 5;
        layout.BuildWidgets(viewport);

        // Visible window must never exceed the viewport height, regardless of how many
        // ChatLines map into it (this is the bug: slicing before expand overflowed).
        await Assert.That(layout.TotalLines - layout.HistoryTopRow).IsLessThanOrEqualTo(viewport);

        // TotalLines is the FULL wrapped content, not the truncated visible window,
        // so scroll math has a correct denominator.
        await Assert.That(layout.TotalLines).IsEqualTo(15);
        await Assert.That(layout.ViewportLines).IsEqualTo(viewport);
    }

    [Test]
    public async Task BuildHistory_ScrollOffsetIsDisplayRowsFromBottom()
    {
        var longMsg = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"a{i}"));
        var lines = ImmutableArray.Create(
            new ChatLine(ChatRole.Assistant, longMsg),
            new ChatLine(ChatRole.User, "tail"));

        var layout = new ChatViewProjector();
        layout.SetLines(lines, isStreaming: false, ActiveMessage.Empty);

        const int viewport = 4;
        // Offset 0 → pinned to tail → first visible row is (TotalLines - viewport).
        layout.ScrollOffset = 0;
        layout.BuildWidgets(viewport);
        int pinnedTop = layout.HistoryTopRow;
        await Assert.That(layout.TotalLines).IsEqualTo(23); // 20 + blank + 1 + blank
        await Assert.That(pinnedTop).IsEqualTo(23 - viewport);

        // Offset lifts N display rows from the bottom; the slice shifts upward.
        layout.ScrollOffset = 10;
        layout.BuildWidgets(viewport);
        await Assert.That(layout.HistoryTopRow).IsEqualTo(23 - viewport - 10);
        await Assert.That(layout.HistoryTopRow).IsNotEqualTo(pinnedTop);
    }

    [Test]
    public async Task BuildHistory_StreamOnlyAppendedWhenPinnedToBottom()
    {
        var lines = ImmutableArray.Create(new ChatLine(ChatRole.User, "u"));
        var layout = new ChatViewProjector();
        layout.SetLines(lines, isStreaming: true, new ActiveMessage("live", "think"));

        // Pinned (offset 0): stream rows are appended → total includes them.
        layout.ScrollOffset = 0;
        layout.BuildWidgets(10);
        int pinnedTotal = layout.TotalLines;

        // Scrolled back: stream must NOT be appended (history stays stable).
        layout.ScrollOffset = 100;
        layout.BuildWidgets(10);
        int scrolledTotal = layout.TotalLines;

        // Source is 1 line (+blank) = 2 display rows; stream adds think+assistant+blanks.
        await Assert.That(pinnedTotal).IsGreaterThan(scrolledTotal);
        await Assert.That(scrolledTotal).IsEqualTo(2);
    }
}

public class SpectreTuiRenderContextTests
{
    [Test]
    public async Task Width_DoesNotThrow()
    {
        var ctx = new SpectreTuiRenderContext();
        try { _ = ctx.Width; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Height_DoesNotThrow()
    {
        var ctx = new SpectreTuiRenderContext();
        try { _ = ctx.Height; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task SupportsColor_IsTrue()
    {
        var ctx = new SpectreTuiRenderContext();
        await Assert.That(ctx.SupportsColor).IsTrue();
    }
}
