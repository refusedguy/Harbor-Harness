using System.Collections.Immutable;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
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

        var tail = ChatAppReducerPlugin.Instance.After(running, new AppMsg.ScrollResetToTail());
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

    /// <summary>
    ///     #773 — a failed compaction is a DEGRADATION notice, not a failed run.
    ///     <c>CompactionBehavior.PublishFailureAsync</c> publishes the event and
    ///     returns <c>TruncationFallback: true</c>; the turn continues on a
    ///     truncated history. So the cell must leave "compacting" (set by the sibling
    ///     <c>CompactionStartedEvent</c> arm) and say "running" — which is what
    ///     <c>OnMessageStart</c> would have said a moment later anyway.
    ///     <para>
    ///         It must NOT be "error". <c>AgentEndEvent</c> deliberately
    ///         preserves an "error" status past the end of a run
    ///         (<c>Status == "error" ? "error" : "idle"</c>), so an "error" here
    ///         would repaint a run that completed as a failed one — a worse and
    ///         permanent lie than the transient one this issue filed.
    ///     </para>
    ///     <para>
    ///         The line is what makes the truncation non-silent: without an arm
    ///         the degradation left no trace in the transcript at all.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Compaction_Failed_LeavesCompacting_WarnsAboutTruncation()
    {
        var started = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new CompactionStartedEvent("s"))).State;
        await Assert.That(started.Chat.Status).IsEqualTo("compacting");

        var failed = ChatAppReducer.Update(started,
            new ChatAppMsg.Agent(new CompactionFailedEvent("s", "summarizer timed out"))).State;

        await Assert.That(failed.Chat.Status).IsEqualTo("running")
            .Because("the turn continues on truncated history — it is not a failed run, and "
                   + "\"error\" would survive AgentEndEvent and misreport a completed run");

        await Assert.That(failed.Chat.Lines.Length).IsEqualTo(1)
            .Because("the truncation fallback is a silent degradation otherwise — the run continues "
                   + "on a shorter history and the transcript never says so");

        await Assert.That(failed.Chat.Lines[0].Role).IsEqualTo(ChatRole.System)
            .Because("a compaction outcome is a system notice, matching OnCompactionCompleted");

        await Assert.That(failed.Chat.Lines[0].Text).Contains("compaction failed")
            .Because("the user has to be able to tell a degraded turn from a normal one");
    }

    /// <summary>
    ///     The failure arm must not claim the run's outcome. #687's
    ///     <c>SessionStatus</c> is decided by the transition that establishes it
    ///     — here the run keeps going, and <c>AgentEndEvent</c> is the fact that
    ///     settles it. Writing an error status here would be the projection
    ///     judging a run it has not seen end.
    /// </summary>
    [Test]
    public async Task Compaction_Failed_DoesNotTouchSessionStatus()
    {
        var started = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new CompactionStartedEvent("s"))).State;

        var failed = ChatAppReducer.Update(started,
            new ChatAppMsg.Agent(new CompactionFailedEvent("s", "summarizer timed out"))).State;

        await Assert.That(failed.Chat.SessionStatus).IsEqualTo(started.Chat.SessionStatus);
    }

    /// <summary>
    ///     #653: the reducer's whole remaining job for money — copy the total the
    ///     core published, cache-aware and model-correct, and nothing else.
    /// </summary>
    [Test]
    public async Task SessionStats_AdoptsTheCostTheCorePriced()
    {
        var state = Stats("s1", new SessionMetadata(
            0.1878m, 1_234, 567, 0, 900, 100, 3, null));

        await Assert.That(state.Chat.Cost.CostUsd).IsEqualTo(0.1878m);
        await Assert.That(state.Chat.Cost.TokensIn).IsEqualTo(1_234);
        await Assert.That(state.Chat.Cost.TokensOut).IsEqualTo(567);
        await Assert.That(state.Chat.Cost.IsCostUnpriced).IsFalse();
    }

    /// <summary>
    ///     #653: the UI computes nothing. A finished LLM step carries usage, and
    ///     the reducer must move no money for it — the old $3/M in, $15/M out
    ///     constants turned exactly this event into a fabricated $0.1878 on a
    ///     free model. The core's totals are the only source.
    ///     <para>
    ///         #651 sharpens the boundary rather than moving it: the step DOES
    ///         record what the context occupies (61 600 prompt tokens is the
    ///         request the provider just accepted), and that is a size, not a
    ///         bill. The paid totals stay untouched until the core publishes them.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StepFinish_MovesNoCost()
    {
        var partial = AssistantMessage.Empty("s", "m");
        var state = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new MessageStartEvent(partial))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(61_600, 196)), partial))).State;

        await Assert.That(state.Chat.Cost.CostUsd).IsEqualTo(0m);
        await Assert.That(state.Chat.Cost.TokensIn).IsEqualTo(0);
        await Assert.That(state.Chat.Cost.TokensOut).IsEqualTo(0);
        await Assert.That(state.Chat.Cost.ContextTokens).IsEqualTo(61_600);
    }

    /// <summary>
    ///     #653: the cost total is ABSOLUTE, not a delta — a second stats event
    ///     replaces the number instead of adding to it, or the status bar would
    ///     report twice what the core said.
    /// </summary>
    [Test]
    public async Task SessionStats_ReplacesTheTotal_ItDoesNotAccumulateIt()
    {
        var state = Stats("s1", new SessionMetadata(0.0105m, 1_000, 500, 0, 0, 0, 1, null));
        state = Stats(state, "s1", new SessionMetadata(0.0210m, 2_000, 1_000, 0, 0, 0, 2, null));

        await Assert.That(state.Chat.Cost.CostUsd).IsEqualTo(0.0210m);
        await Assert.That(state.Chat.Cost.TokensIn).IsEqualTo(2_000);
    }

    /// <summary>
    ///     #653: an unpriced model is reported as unpriced. The status bar reads
    ///     "—" here, which is the whole point of the core publishing a bit
    ///     instead of a zero that would read as "free".
    /// </summary>
    [Test]
    public async Task SessionStats_UnknownPrice_MarksTheCostAsUnknown()
    {
        var state = Stats("s1", new SessionMetadata(
            0m, 10_000, 2_000, 0, 0, 0, 1, null, IsCostKnown: false));

        await Assert.That(state.Chat.Cost.IsCostUnpriced).IsTrue();
        await Assert.That(StatusBarText.CostCell(state.Chat.Cost.CostUsd, state.Chat.Cost.IsCostUnpriced))
            .IsEqualTo("—");
    }

    /// <summary>
    ///     #653: a state that never mentioned a cost renders exactly as it did
    ///     before the flag existed. <c>CostSnapshot</c> is a struct, so
    ///     <c>default</c> is what <c>ChatDomainState.Empty</c>, a fresh
    ///     <c>UiState</c> and half the test suite carry; had the flag been
    ///     "priced", every one of those screens would have read "price unknown".
    /// </summary>
    [Test]
    public async Task AnUnsetCostSnapshot_ReadsAsPriced()
    {
        var state = new UiState();

        await Assert.That(state.Chat.Cost.IsCostUnpriced).IsFalse();
        await Assert.That(StatusBarText.CostCell(state.Chat.Cost.CostUsd, state.Chat.Cost.IsCostUnpriced))
            .IsNull();
    }

    private static UiState Stats(string sessionId, SessionMetadata metadata) =>
        Stats(new UiState(), sessionId, metadata);

    private static UiState Stats(UiState state, string sessionId, SessionMetadata metadata) =>
        ChatAppReducer.Update(state, new ChatAppMsg.Agent(new SessionStatsEvent(sessionId, metadata))).State;

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
