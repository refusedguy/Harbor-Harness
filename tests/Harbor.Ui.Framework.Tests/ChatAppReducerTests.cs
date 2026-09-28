using System.Collections.Immutable;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Acceptance tests for the HARBOR half of the TEA split (#33/T4, #364):
///     <see cref="ChatAppReducer" /> and its <see cref="IAppReducerPlugin" />
///     hook. Complements <see cref="AppReducerGenericTests" /> (which stays
///     domain-free) — together they pin both halves of the split.
/// </summary>
public class ChatAppReducerTests
{
    private static UiState Running =>
        new() { Chat = ChatDomainState.Empty with { IsAgentRunning = true, Status = "running" } };

    // ── extension plumbing ─────────────────────────────────────────────────

    [Test]
    public async Task Plugin_ClaimsChatArms_AndDeclinesGenericOnes()
    {
        var claimed = ChatAppReducerPlugin.Instance.Reduce(new UiState(), new ChatAppMsg.AgentStarted());
        await Assert.That(claimed).IsNotNull();
        await Assert.That(claimed!.Value.State.Chat.IsAgentRunning).IsTrue();

        var declined = ChatAppReducerPlugin.Instance.Reduce(new UiState(), new AppMsg.Viewport(10));
        await Assert.That(declined).IsNull();
    }

    [Test]
    public async Task Plugin_ReportsBusyOnlyWhileAgentRuns()
    {
        await Assert.That(ChatAppReducerPlugin.Instance.IsBusy(new UiState())).IsFalse();
        await Assert.That(ChatAppReducerPlugin.Instance.IsBusy(Running)).IsTrue();
    }

    [Test]
    public async Task Plugin_AfterHook_OnlyFoldsOnScrollResetToTail()
    {
        var running = Running;

        var tail = ChatAppReducerPlugin.Instance.After(running, new AppMsg.ScrollResetToTail);
        await Assert.That(tail.Chat.WasRunning).IsTrue();

        var other = ChatAppReducerPlugin.Instance.After(running, new AppMsg.Viewport(10));
        await Assert.That(other).IsSameReferenceAs(running);
    }

    [Test]
    public async Task ComposedUpdate_AppliesGenericArms_WithoutChatPlugin()
    {
        var result = AppReducer.Update(new UiState(), new AppMsg.Viewport(12));
        await Assert.That(result.State.Ui.ViewportLines).IsEqualTo(12);
        // No extension ⇒ the generic reducer alone never touches the chat part.
        await Assert.That(result.State.Chat).IsEqualTo(ChatDomainState.Empty);
    }

    // ── busy gate on keys ──────────────────────────────────────────────────

    [Test]
    public async Task KeyInput_WhileRunning_EditingIsSuppressed()
    {
        var result = ChatAppReducer.Update(Running,
            new AppMsg.KeyInput(ChatAction.Char, UiKey.ForChar('x')));
        await Assert.That(result.State.Ui.Input.Text).IsEmpty();
    }

    [Test]
    public async Task KeyInput_WhileRunning_SubmitIsSuppressed()
    {
        var state = new UiState
        {
            Ui = TerminalUiState.Empty with { Input = new InputModel("hi", [], -1) },
            Chat = ChatDomainState.Empty with { IsAgentRunning = true, Status = "running" }
        };
        var result = ChatAppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        await Assert.That(result.State.Ui.Input.Text).IsEqualTo("hi");
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.None>();
    }

    [Test]
    public async Task KeyInput_WhileRunning_QuitBecomesAbort()
    {
        var result = ChatAppReducer.Update(Running,
            new AppMsg.KeyInput(ChatAction.Quit, new UiKey(UiKeyCode.Escape)));
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.AbortAgent>();
    }

    [Test]
    public async Task KeyInput_WhileIdle_QuitEmitsQuitApp()
    {
        var result = ChatAppReducer.Update(new UiState(),
            new AppMsg.KeyInput(ChatAction.Quit, new UiKey(UiKeyCode.Escape)));
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.QuitApp>();
    }

    [Test]
    public async Task KeyInput_Submit_SlashCommandEmitsRunSlash()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { Input = new InputModel("/models", [], -1) } };
        var result = ChatAppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.RunSlash>();
    }

    [Test]
    public async Task KeyInput_Submit_ExitWordEmitsQuitApp()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { Input = new InputModel("quit", [], -1) } };
        var result = ChatAppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));
        await Assert.That(result.Effect).IsTypeOf<TuiEffect.QuitApp>();
    }

    [Test]
    public async Task KeyInput_Clear_KeepsSessionChromeAndDropsTranscript()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Model = "m",
                Provider = "p",
                AgentName = "code",
                Status = "running"
            }
        };
        state = ChatAppReducer.Update(state, new ChatAppMsg.AppendLine(ChatRole.User, "hi")).State;
        state = ChatAppReducer.Update(state, new AppMsg.Viewport(20)).State;
        state = ChatAppReducer.Update(state, new AppMsg.HistoryMeasured(1)).State;

        var cleared = ChatAppReducer.Update(state, new AppMsg.KeyInput(ChatAction.Clear, new UiKey(UiKeyCode.None)));

        await Assert.That(cleared.State.Chat.Lines).IsEmpty();
        await Assert.That(cleared.State.Chat.Model).IsEqualTo("m");
        await Assert.That(cleared.State.Chat.Provider).IsEqualTo("p");
        await Assert.That(cleared.State.Chat.AgentName).IsEqualTo("code");
        await Assert.That(cleared.State.Ui.TotalLines).IsEqualTo(0);
    }

    // ── agent events ───────────────────────────────────────────────────────

    [Test]
    public async Task AgentStart_PinsScrollToTail_AndReplaysEmptyTranscript()
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 40 } };
        var history = new AgentMessage[]
        {
            new UserMessage("u1", "s", DateTimeOffset.UtcNow, "hi", "code", "m", null),
        };

        var result = ChatAppReducer.Update(state,
            new ChatAppMsg.Agent(new AgentStartEvent("s", history)));

        await Assert.That(result.State.Ui.ScrollOffset).IsEqualTo(0);
        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(result.State.Chat.Lines[0].Role).IsEqualTo(ChatRole.User);
    }

    [Test]
    public async Task MessageStream_FoldsDeltasIntoTranscriptOnMessageEnd()
    {
        var partial = AssistantMessage.Empty("s", "m");
        var state = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new MessageStartEvent(partial))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            new MessageUpdateEvent(new TextDeltaEvent("m", "hello"), partial))).State;

        await Assert.That(state.Chat.IsStreaming).IsTrue();
        await Assert.That(state.Chat.Active.TextBuffer).IsEqualTo("hello");

        var ended = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new MessageEndEvent(partial)));
        await Assert.That(ended.State.Chat.IsStreaming).IsFalse();
        await Assert.That(ended.State.Chat.Lines[^1].Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(ended.State.Chat.Lines[^1].Text).IsEqualTo("hello");
    }

    [Test]
    public async Task ToolExecution_AppendsToolAndResultLines()
    {
        using var args = JsonDocument.Parse("{\"path\":\"a.cs\"}");
        var state = ChatAppReducer.Update(new UiState(), new ChatAppMsg.Agent(
            ToolExecutionStartEvent.Create("tc1", "read", args.RootElement))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            new ToolExecutionEndEvent("tc1", new ToolResult("ok", false), false))).State;

        await Assert.That(state.Chat.Lines[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(state.Chat.Lines[0].ToolCallId).IsEqualTo("tc1");
        await Assert.That(state.Chat.Lines[1].Role).IsEqualTo(ChatRole.ToolResult);
    }

    [Test]
    public async Task Compaction_Started_SetsCompactingStatus()
    {
        var result = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new CompactionStartedEvent("s")));
        await Assert.That(result.State.Chat.Status).IsEqualTo("compacting");
    }

    [Test]
    public async Task StepFinish_AccumulatesCost()
    {
        var partial = AssistantMessage.Empty("s", "m");
        var state = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new MessageStartEvent(partial))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(1_000, 2_000)), partial))).State;

        await Assert.That(state.Chat.Cost.TokensIn).IsEqualTo(1_000);
        await Assert.That(state.Chat.Cost.TokensOut).IsEqualTo(2_000);
        await Assert.That(state.Chat.Cost.CostUsd).IsGreaterThan(0m);
    }

    [Test]
    public async Task AgentEnd_AfterError_KeepsErrorStatus()
    {
        var state = ChatAppReducer.Update(Running,
            new ChatAppMsg.Agent(new AgentErrorEvent("boom"))).State;
        var ended = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([])));
        await Assert.That(ended.State.Chat.Status).IsEqualTo("error");
        await Assert.That(ended.State.Chat.IsAgentRunning).IsFalse();
    }

    // ── chat messages ──────────────────────────────────────────────────────

    [Test]
    public async Task HydrateSession_ReplacesTranscriptAndChromeInOneTransition()
    {
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.HydrateSession(
            "m", "p", "code",
            ImmutableArray.Create(
                new ChatLine(ChatRole.User, "u"),
                new ChatLine(ChatRole.Assistant, "a"))));

        await Assert.That(result.State.Chat.Lines.Length).IsEqualTo(2);
        await Assert.That(result.State.Chat.Model).IsEqualTo("m");
        await Assert.That(result.State.Chat.AgentName).IsEqualTo("code");
    }

    [Test]
    public async Task SyncSessions_WritesSessionListIntoTheChatPart()
    {
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.SyncSessions(
            ImmutableArray<SessionInfo>.Empty, null));
        await Assert.That(result.State.Chat.Sessions).IsEmpty();
        await Assert.That(result.State.Chat.ActiveSessionId).IsNull();
    }

    [Test]
    public async Task StatusChanged_WritesOnlyTheStatus()
    {
        var result = ChatAppReducer.Update(Running, new ChatAppMsg.StatusChanged("idle"));
        await Assert.That(result.State.Chat.Status).IsEqualTo("idle");
        await Assert.That(result.State.Chat.IsAgentRunning).IsTrue();
    }
}
