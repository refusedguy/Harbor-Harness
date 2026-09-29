// SessionStatusSingleSourceTests.cs — behaviour of #687.
//
// The rule (a status is decided once, at the transition, and read afterwards)
// is enforced structurally by
// tests/Harbor.Architecture.Tests/SessionStatusSourceRule.cs. These tests pin
// the BEHAVIOUR that structural rule protects.
//
// The old answer came from a function of the transcript:
//
//   running → Working ;  Status == "error" → Error ;
//   last line is assistant → Done ;  otherwise → Idle
//
// So it disagreed with the core in every case where the core's answer is not a
// function of the last line's role. Three of those are pinned below, and each
// test names which of the two it is:
//
//   1. a run that finished on a TOOL RESULT, not an assistant line → the
//      heuristic said Idle, the core said Done, so a session that had genuinely
//      completed never turned green;
//   2. a run the user CANCELLED, whose last line is the assistant's
//      half-sentence → the heuristic said Done (a green dot on a run the user
//      stopped), the core says Aborted;
//   3. any status the reducer decided → the presenter recomputed it from the
//      transcript on every 16 ms render frame and pushed that second opinion
//      into SessionStatusTracker over the top of the first.
//
// NOT claimed here: SessionLifecycleService writes Error straight into
// SessionStatusTracker (a branch that would not open), bypassing the store, so
// the next frame still replaces that value — now with the reducer's answer
// rather than a transcript guess. Unifying that writer is its own change; see
// the note on WhateverTheReducerDecided_IsWhatTheNextFramePushes.

using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #687: a session's status comes from the run that established it, never
///     from the role of the transcript's last line.
/// </summary>
public class SessionStatusSingleSourceTests
{
    private static readonly ChatStreamingPresenter Presenter = new();

    /// <summary>Drive a run that streamed one assistant message and ended it.</summary>
    private static UiState StreamedAssistantMessage(UiState? from = null)
    {
        UiState state = from ?? ChatAppReducer.Update(new UiState(), new ChatAppMsg.AgentStarted()).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            new MessageStartEvent(AssistantMessage.Empty("s", "m")))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            new MessageUpdateEvent(new TextDeltaEvent("m", "the answer"),
                AssistantMessage.Empty("s", "m")))).State;
        return ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            new MessageEndEvent(AssistantMessage.Empty("s", "m")))).State;
    }

    // ── 1. a clean finish is Done whatever the last line is ────────────────

    [Test]
    public async Task CleanRunEndingOnAToolResult_IsDone_NotIdle()
    {
        // A turn whose last word in the transcript is a tool result. The core
        // published AgentEndEvent and said the run finished; the heuristic
        // looked at the tail, saw ToolResult, and said Idle — so a session that
        // had genuinely completed never turned green.
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state, new ChatAppMsg.AppendLine(
            ChatRole.Tool, "→ read")).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.AppendLine(
            ChatRole.ToolResult, "✓ file contents", "tc1")).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([]))).State;

        await Assert.That(state.Chat.Lines[^1].Role).IsEqualTo(ChatRole.ToolResult)
            .Because("this test is about a transcript whose tail is NOT an assistant line. The old "
                   + "heuristic answered Done only when the tail was an assistant line, so this tail "
                   + "is the case that made it say Idle");
        await Assert.That(state.Chat.Status).IsEqualTo("idle");
        await Assert.That(state.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);
    }

    // ── 2. a cancelled run is Aborted, not a green "done" ──────────────────

    [Test]
    public async Task CancelledRun_IsAborted_EvenThoughTheLastLineIsAssistant()
    {
        // The shape the issue describes: the run stops mid-sentence, so the
        // transcript's last line is the assistant's half-answer. The heuristic
        // read that as a completed turn and painted the session dot green.
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state,
            new ChatAppMsg.Agent(new AgentEndEvent([], Cancelled: true))).State;

        await Assert.That(state.Chat.Lines[^1].Role).IsEqualTo(ChatRole.Assistant)
            .Because("this test is about a transcript whose tail IS an assistant line — the exact "
                   + "input the old heuristic's third arm keyed on");
        await Assert.That(state.Chat.SessionStatus).IsEqualTo(SessionStatus.Aborted);
    }

    // ── 3. the presenter adds no opinion of its own ────────────────────────

    [Test]
    public async Task WhateverTheReducerDecided_IsWhatTheNextFramePushes()
    {
        // The overwriting half of #687, at the seam that does it.
        // ChatViewModel.RenderFrameTick calls DeriveStatus on every 16 ms frame
        // and writes the answer into SessionStatusTracker, so the presenter is
        // the last writer standing on that tracker's value. A presenter that
        // recomputed from the transcript would replace whatever the reducer had
        // decided on the previous frame — including a status this test set by
        // hand, standing in for any writer the reducer does not own.
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([]))).State;

        var decided = state with
        {
            Chat = state.Chat with { SessionStatus = SessionStatus.Error }
        };

        await Assert.That(Presenter.DeriveStatus(decided)).IsEqualTo(SessionStatus.Error)
            .Because("the presenter is a read; recomputing here is what let a transcript guess "
                   + "overwrite a status one frame after it was written");

        // NOT fixed by #687, recorded rather than claimed: SessionLifecycleService
        // writes Error into SessionStatusTracker directly (on a branch that
        // would not open), not through the store, so the next frame still
        // replaces it — now with the reducer's answer instead of a transcript
        // guess. Unifying that writer is a separate change; this test pins only
        // that the projection layer stopped contributing a second opinion.
        await Assert.That(Presenter.DeriveStatus(state)).IsEqualTo(SessionStatus.Done)
            .Because("without a hand-set value the reducer's own decision is what the frame reads");
    }

    // ── the other terminals ────────────────────────────────────────────────

    [Test]
    public async Task CoreReportedFailure_IsError()
    {
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(
            new AgentErrorEvent("provider exploded"))).State;
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([]))).State;

        await Assert.That(state.Chat.SessionStatus).IsEqualTo(SessionStatus.Error);
    }

    [Test]
    public async Task HostReportedFailure_IsError_WithNoAgentErrorEventOnTheBus()
    {
        // TuiEffectHost's Result-failure branch dispatches AgentEnded("error", …)
        // when the agent RETURNS a failure rather than throwing. Nothing on the
        // event bus said so; the host's message is the only statement there is.
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state,
            new ChatAppMsg.AgentEnded("error", "agent returned a failure")).State;

        await Assert.That(state.Chat.SessionStatus).IsEqualTo(SessionStatus.Error);
    }

    [Test]
    public async Task CleanRun_IsDone_AndTheHostClosingItOutDoesNotUndoIt()
    {
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([]))).State;
        await Assert.That(state.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);

        // TuiEffectHost.PromptAsync dispatches AgentEnded() from its finally
        // block on the SUCCESS path, after the core already published the
        // terminal. Reading the host's message as a second verdict downgraded
        // Done to Idle one message later, so the dot never settled.
        var closedOut = ChatAppReducer.Update(state, new ChatAppMsg.AgentEnded()).State;
        await Assert.That(closedOut.Chat.SessionStatus).IsEqualTo(SessionStatus.Done)
            .Because("the host closes a run out, it does not re-judge it");
    }

    [Test]
    public async Task RunStillWorking_WhenTheHostClosesItOut_BecomesIdle_NotWorking()
    {
        // A run the host abandons without a terminal ever being published.
        // Nothing is running and nothing succeeded, so Idle is the honest
        // reading; staying Working is the "hangs in thinking forever" failure
        // the host's finally block exists to prevent.
        var running = ChatAppReducer.Update(new UiState(), new ChatAppMsg.AgentStarted()).State;
        await Assert.That(running.Chat.SessionStatus).IsEqualTo(SessionStatus.Working);

        var closedOut = ChatAppReducer.Update(running, new ChatAppMsg.AgentEnded()).State;
        await Assert.That(closedOut.Chat.SessionStatus).IsEqualTo(SessionStatus.Idle);
    }

    [Test]
    public async Task NewRun_IsWorking_FromEveryEntryPoint()
    {
        var viaHost = ChatAppReducer.Update(new UiState(), new ChatAppMsg.AgentStarted()).State;
        await Assert.That(viaHost.Chat.SessionStatus).IsEqualTo(SessionStatus.Working);

        var viaCore = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.Agent(new AgentStartEvent("s", []))).State;
        await Assert.That(viaCore.Chat.SessionStatus).IsEqualTo(SessionStatus.Working);

        var viaMessage = ChatAppReducer.Update(viaCore,
            new ChatAppMsg.Agent(new MessageStartEvent(AssistantMessage.Empty("s", "m")))).State;
        await Assert.That(viaMessage.Chat.SessionStatus).IsEqualTo(SessionStatus.Working);
    }

    [Test]
    public async Task Compaction_DoesNotMoveTheStatus()
    {
        // "compacting" is a status-bar state, not a session state: the run is
        // still going. The old code read Status == "error" first and so never
        // reached the tail check — it was right by accident, not by design.
        var state = ChatAppReducer.Update(new UiState(), new ChatAppMsg.AgentStarted()).State;
        state = ChatAppReducer.Update(state,
            new ChatAppMsg.Agent(new CompactionStartedEvent("s"))).State;

        await Assert.That(state.Chat.Status).IsEqualTo("compacting");
        await Assert.That(state.Chat.SessionStatus).IsEqualTo(SessionStatus.Working);
    }

    // ── the switch path reads the core's stored answer ─────────────────────

    [Test]
    public async Task HydrateSession_AdoptsThePersistedStatus_NotATranscriptGuess()
    {
        // SubAgentRunner stamps Done around a real sub-agent run and persists
        // it. Re-deriving from the replayed lines is the same defect on the
        // switch path: the tail of a replayed transcript is not what the core
        // recorded about the run.
        var result = ChatAppReducer.Update(new UiState(), new ChatAppMsg.HydrateSession(
            "m", "p", "code",
            ImmutableArray.Create(
                new ChatLine(ChatRole.User, "u"),
                new ChatLine(ChatRole.Assistant, "a")),
            SessionStatus.Done));

        await Assert.That(result.State.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);
    }

    [Test]
    public async Task HydrateSession_DefaultsToIdle_ForACallerThatPassesNoStatus()
    {
        // The defaulted parameter keeps existing dispatch sites compiling, and
        // the default is the honest one (nothing known) rather than a guess.
        var result = ChatAppReducer.Update(new UiState(),
            new ChatAppMsg.HydrateSession("m", "p", "code", []));

        await Assert.That(result.State.Chat.SessionStatus).IsEqualTo(SessionStatus.Idle);
    }

    [Test]
    public async Task ClearTranscript_KeepsTheStatusTheRunEstablished()
    {
        // A clear-screen drops what was said, not what happened. Before #687
        // there was no value to lose: the status was a function of the
        // transcript, so clearing the transcript was how you changed it.
        var state = StreamedAssistantMessage();
        state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(new AgentEndEvent([]))).State;

        var cleared = ChatAppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Clear, new UiKey(UiKeyCode.None))).State;

        await Assert.That(cleared.Chat.Lines.Length).IsEqualTo(0);
        await Assert.That(cleared.Chat.SessionStatus).IsEqualTo(SessionStatus.Done);
    }

    // ── the presenter is a read, and only a read ───────────────────────────

    [Test]
    public async Task Presenter_ReturnsTheStateField_AndNothingElse()
    {
        // Walk all five outcomes against a transcript that would have driven the
        // OLD heuristic to Done (ends on an assistant line, not running, no
        // error string), so a presenter that recomputed anything cannot pass.
        SessionStatus[] expected =
        [
            SessionStatus.Idle, SessionStatus.Working, SessionStatus.Done,
            SessionStatus.Error, SessionStatus.Aborted,
        ];

        foreach (SessionStatus status in expected)
        {
            var state = new UiState
            {
                Chat = ChatDomainState.Empty with
                {
                    SessionStatus = status,
                    Lines = ImmutableArray.Create(new ChatLine(ChatRole.Assistant, "a")),
                }
            };

            await Assert.That(Presenter.DeriveStatus(state)).IsEqualTo(status)
                .Because("the presenter reads ChatDomainState.SessionStatus and nothing else");
        }
    }
}
