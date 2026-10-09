using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Storage.Tests;

/// <summary>
///     Epic #41 slice B4.1 (#406): honest recovery. A run that ended WITHOUT a
///     terminal event (killed process, crash, lost connection) is reported as
///     <see cref="RunTermination.Interrupted" /> with its last confirmed state —
///     never as <see cref="RunStopReason.Failed" /> — and resume is offered
///     only from a safe confirmed boundary, per run, with a reason.
/// </summary>
/// <remarks>
///     <para>
///         No real process kills, no wall-clock timing: every test builds a
///         message history, declares a deterministic kill point (which suffix
///         is unconfirmed), and drives detection from that. The store side is a
///         re-read pair (observed vs. re-read-from-store), so "verified by
///         read-back, not assumed" is exercised, not asserted.
///     </para>
///     <para>
///         Non-vacuity note: on a tree without the marker these tests fail by
///         assertion, not by compilation — the red commit carries a stub that
///         answers <c>Terminal</c>/null/empty so the build stays green while
///         the discriminating assertions go red. Three tests pass on the stub
///         on purpose (<c>ErrorPlusTerminalEvent</c>, <c>FreshSession</c>,
///         <c>UnknownBackend</c>): they prove the detector is not "always
///         interrupt" and guard what the fix must preserve.
///     </para>
/// </remarks>
public class RunRecoveryTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement JsonArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static UserMessage NewUser(string sessionId, int seq, string model = "kilo-auto") => new(
        $"u-406-{seq}",
        sessionId,
        BaseTime.AddSeconds(seq),
        "Read README.md",
        "code",
        model);

    private static AssistantMessage NewTextAssistant(string sessionId, int seq, string text = "Done.") => new(
        $"a-406-{seq}",
        sessionId,
        BaseTime.AddSeconds(seq),
        new ContentPart[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(10, 5),
        "kilo-auto");

    private static AssistantMessage NewAssistantWithToolCall(string sessionId, int seq, string toolCallId, string toolName) => new(
        $"a-406-{seq}",
        sessionId,
        BaseTime.AddSeconds(seq),
        new ContentPart[] { new ToolCallPart(toolCallId, toolName, JsonArgs("""{"path":"README.md"}""")) },
        StopReason.ToolUse,
        new Usage(10, 5),
        "kilo-auto");

    private static ToolResultMessage NewToolResult(string sessionId, int seq, string toolCallId, string toolName) => new(
        $"t-406-{seq}",
        sessionId,
        BaseTime.AddSeconds(seq),
        new[] { new ToolResultEntry(toolCallId, toolName, "file contents", false) });

    private static UnfinishedRunReport DetectInterrupted(
        IReadOnlyList<AgentMessage> observed,
        Func<string, bool>? confirm = null,
        bool hasRunMarker = true)
    {
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", observed,
            hasTerminalEvent: false, isLive: false, hasRunMarker: hasRunMarker,
            isConfirmedByReadBack: confirm ?? (id => observed.Any(m => m.Id == id)));
        return report!;
    }

    [Test]
    public async Task Classify_TerminalEventWinsAndLivenessDecidesTheRest()
    {
        // The marker is orthogonal to the stop reason: the terminal event
        // alone decides Terminal; otherwise liveness decides.
        await Assert.That(UnfinishedRunDetector.Classify(hasTerminalEvent: true, isLive: true)).IsEqualTo(RunTermination.Terminal);
        await Assert.That(UnfinishedRunDetector.Classify(hasTerminalEvent: true, isLive: false)).IsEqualTo(RunTermination.Terminal);
        await Assert.That(UnfinishedRunDetector.Classify(hasTerminalEvent: false, isLive: true)).IsEqualTo(RunTermination.InProgress);
        await Assert.That(UnfinishedRunDetector.Classify(hasTerminalEvent: false, isLive: false)).IsEqualTo(RunTermination.Interrupted);
    }

    [Test]
    public async Task MessagesWithoutTerminalEvent_DetectAsInterruptedNotFailed()
    {
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2),
        };

        var report = DetectInterrupted(observed);

        await Assert.That(report is null).IsFalse();
        await Assert.That(report!.Termination).IsEqualTo(RunTermination.Interrupted);
    }

    [Test]
    public async Task ErrorTextWithoutTerminalEvent_IsStillInterrupted()
    {
        // #406: the error/no-error axis is not the "did the process survive"
        // axis. An error string with no terminal event is transcript content,
        // not a verdict — termination leads.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2, "provider stream broke: boom"),
        };

        var report = DetectInterrupted(observed);

        await Assert.That(report is null).IsFalse();
        await Assert.That(report!.Termination).IsEqualTo(RunTermination.Interrupted);
    }

    [Test]
    public async Task ErrorPlusTerminalEvent_ReconstructStillFailedAndNoUnfinishedEntry()
    {
        // Non-interference with B1: a run that DID reach its terminal event
        // with an error is Failed, and detection has no unfinished entry.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2, "boom"),
        };
        var end = new AgentEndEvent(observed, Cancelled: false);
        var error = new AgentErrorEvent("provider died");

        var outcome = RunOutcome.Reconstruct(RunId.New(), "s-406", observed, end, error);
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", observed,
            hasTerminalEvent: true, isLive: false, hasRunMarker: true,
            isConfirmedByReadBack: _ => true);

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Failed);
        await Assert.That(report is null).IsTrue();
    }

    [Test]
    public async Task FreshSession_EmptyHistoryNoMarker_NoEntry()
    {
        // #993's trap in the other direction: a session that never ran must
        // not be reported as interrupted. No false positives.
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", Array.Empty<AgentMessage>(),
            hasTerminalEvent: false, isLive: false, hasRunMarker: false,
            isConfirmedByReadBack: _ => true);

        await Assert.That(report is null).IsTrue();
    }

    [Test]
    public async Task EmptyHistoryWithRunMarker_IsInterruptedWithNothingConfirmed()
    {
        // The run began (marker persisted) but no message did: interrupted,
        // with an honestly empty confirmed state — not a guessed one.
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", Array.Empty<AgentMessage>(),
            hasTerminalEvent: false, isLive: false, hasRunMarker: true,
            isConfirmedByReadBack: _ => true);

        await Assert.That(report is null).IsFalse();
        await Assert.That(report!.Termination).IsEqualTo(RunTermination.Interrupted);
        await Assert.That(report.LastConfirmed.ConfirmedMessageId is null).IsTrue();
        await Assert.That(report.LastConfirmed.ConfirmedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GarbledTail_RereadReportsPreviousMessageAsLastConfirmed()
    {
        // The kill point: the last message was partially written and re-reads
        // garbled. Detection must name the PREVIOUS message id exactly —
        // the garbled one is reported in the tail, never trusted.
        var first = NewUser("s-406", 1);
        var second = NewTextAssistant("s-406", 2);
        IReadOnlyList<AgentMessage> observed = new AgentMessage[] { first, second };
        IReadOnlyList<AgentMessage> reread = new AgentMessage[]
        {
            first,
            NewTextAssistant("s-406", 2, "Don\uFFFD\uFFFD garbled tail"),
        };

        var confirm = UnfinishedRunDetector.ConfirmByReadBack(observed, reread);
        var state = UnfinishedRunDetector.ConfirmLastState(observed, confirm);

        await Assert.That(state.IsUnknown).IsFalse();
        await Assert.That(state.ConfirmedMessageId).IsEqualTo(first.Id);
        await Assert.That(state.ConfirmedCount).IsEqualTo(1);
        await Assert.That(state.UnconfirmedTailIds.Count).IsEqualTo(1);
        await Assert.That(state.UnconfirmedTailIds[0]).IsEqualTo(second.Id);
    }

    [Test]
    public async Task TruncatedTail_MissingLastMessage_ReportsPrevious()
    {
        // Truncation variant: the tail line never landed, so the re-read is
        // shorter. Same verdict shape as garbling — absence is not evidence.
        var first = NewUser("s-406", 1);
        var second = NewTextAssistant("s-406", 2);
        IReadOnlyList<AgentMessage> observed = new AgentMessage[] { first, second };
        IReadOnlyList<AgentMessage> reread = new AgentMessage[] { first };

        var confirm = UnfinishedRunDetector.ConfirmByReadBack(observed, reread);
        var state = UnfinishedRunDetector.ConfirmLastState(observed, confirm);

        await Assert.That(state.ConfirmedMessageId).IsEqualTo(first.Id);
        await Assert.That(state.UnconfirmedTailIds.Count).IsEqualTo(1);
        await Assert.That(state.UnconfirmedTailIds[0]).IsEqualTo(second.Id);
    }

    [Test]
    public async Task BackendWithoutReadBack_ReportsUnknownRatherThanGuessing()
    {
        // A backend that cannot distinguish full from partial writes passes
        // null and gets Unknown — "could not read", not a fabricated state.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2),
        };

        var state = UnfinishedRunDetector.ConfirmLastState(observed, isConfirmedByReadBack: null);

        await Assert.That(state.IsUnknown).IsTrue();
        await Assert.That(state.ConfirmedMessageId is null).IsTrue();
    }

    [Test]
    public async Task Policy_UnknownReadBack_IsNotResumable()
    {
        // No verified boundary means no safe place to continue from:
        // "could not read" refuses, it does not guess resumable.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2),
        };
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", observed,
            hasTerminalEvent: false, isLive: false, hasRunMarker: true,
            isConfirmedByReadBack: null);

        await Assert.That(report is null).IsFalse();
        await Assert.That(report!.LastConfirmed.IsUnknown).IsTrue();
        var decision = RunRecoveryPolicy.Evaluate(report);
        await Assert.That(decision.IsResumable).IsFalse();
        await Assert.That(string.IsNullOrEmpty(decision.Reason)).IsFalse();
    }

    [Test]
    public async Task Policy_InterruptedBeforeAnyToolCall_IsResumable()
    {
        // Case (a): text-only turns, no tool call issued — nothing is known
        // to have executed, so continuing cannot duplicate a side effect.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2),
        };

        var decision = RunRecoveryPolicy.Evaluate(DetectInterrupted(observed));

        await Assert.That(decision.IsResumable).IsTrue();
        await Assert.That(string.IsNullOrEmpty(decision.Reason)).IsFalse();
    }

    [Test]
    public async Task Policy_KnownCompletedToolResult_IsResumable()
    {
        // Case (b): every issued tool call has a confirmed result and the
        // turn is closed — resume continues AFTER the boundary.
        const string callId = "tc-406-known";
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewAssistantWithToolCall("s-406", 2, callId, "read"),
            NewToolResult("s-406", 3, callId, "read"),
        };

        var report = DetectInterrupted(observed);
        var decision = RunRecoveryPolicy.Evaluate(report);

        await Assert.That(report.Boundary).IsEqualTo(RecoveryBoundary.AtToolResultBoundary);
        await Assert.That(decision.IsResumable).IsTrue();
        await Assert.That(string.IsNullOrEmpty(decision.Reason)).IsFalse();
    }

    [Test]
    public async Task Policy_InFlightToolCall_IsNotResumableWithReason()
    {
        // Case (c): the call persisted but its result did not — the default
        // is refusal with a non-empty reason naming the unknown outcome.
        const string callId = "tc-406-inflight";
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewAssistantWithToolCall("s-406", 2, callId, "bash"),
        };

        var report = DetectInterrupted(observed);
        var decision = RunRecoveryPolicy.Evaluate(report);

        await Assert.That(report.Boundary).IsEqualTo(RecoveryBoundary.InFlightToolCall);
        await Assert.That(decision.IsResumable).IsFalse();
        await Assert.That(string.IsNullOrEmpty(decision.Reason)).IsFalse();
        await Assert.That(decision.Reason.Contains(callId)).IsTrue();
        await Assert.That(decision.Reason.Contains("bash")).IsTrue();
    }

    [Test]
    public async Task Policy_MidModelCall_IsNotResumableAndNamesTheModelCall()
    {
        // Case (d): killed while the model call was outstanding — no
        // persisted response, so what it did is unknowable. The reason names
        // the targeted model, not just "unknown".
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1, model: "kilo-auto"),
        };

        var report = DetectInterrupted(observed);
        var decision = RunRecoveryPolicy.Evaluate(report);

        await Assert.That(report.Boundary).IsEqualTo(RecoveryBoundary.MidModelCall);
        await Assert.That(decision.IsResumable).IsFalse();
        await Assert.That(string.IsNullOrEmpty(decision.Reason)).IsFalse();
        await Assert.That(decision.Reason.Contains("kilo-auto")).IsTrue();
    }

    [Test]
    public async Task Policy_NonIdempotentRefusal_CarriesQualifiedDisclaimer()
    {
        // Exactly-once is disclaimed, never promised: the refusal for an
        // effectful tool carries the qualified sentence, and the bare
        // word appears nowhere unqualified.
        const string callId = "tc-406-bash";
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewAssistantWithToolCall("s-406", 2, callId, "bash"),
        };

        var decision = RunRecoveryPolicy.Evaluate(DetectInterrupted(observed));

        await Assert.That(decision.IsResumable).IsFalse();
        await Assert.That(decision.Reason.Contains(RunRecoveryPolicy.NoExactlyOnceDisclaimer)).IsTrue();
        await Assert.That(decision.Reason.Contains("cannot be guaranteed")).IsTrue();
    }

    [Test]
    public async Task Policy_LiveRun_IsNotResumable()
    {
        // Resume does not apply to a running run: InProgress waits, it does
        // not fork. A second concurrent resume is the blind retry by another
        // name.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2),
        };
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", observed,
            hasTerminalEvent: false, isLive: true, hasRunMarker: true,
            isConfirmedByReadBack: _ => true);

        await Assert.That(report is null).IsFalse();
        await Assert.That(report!.Termination).IsEqualTo(RunTermination.InProgress);
        await Assert.That(RunRecoveryPolicy.Evaluate(report).IsResumable).IsFalse();
    }

    [Test]
    public async Task Retry_IsNewRunAndLeavesParentUnchanged()
    {
        // A retry is a NEW run with a link to the parent; the interrupted
        // record is never mutated — records make this free, the test pins it.
        const string callId = "tc-406-retry";
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewAssistantWithToolCall("s-406", 2, callId, "read"),
            NewToolResult("s-406", 3, callId, "read"),
        };
        var parent = DetectInterrupted(observed);
        var snapshot = parent;

        var link = UnfinishedRunDetector.CreateRetry(parent);

        await Assert.That(parent).IsEqualTo(snapshot);
        await Assert.That(link.NewRunId.Value == parent.RunId.Value).IsFalse();
        await Assert.That(link.ParentRunId).IsEqualTo(parent.RunId);
        await Assert.That(link.ResumedFromMessageId).IsEqualTo(parent.LastConfirmed.ConfirmedMessageId);
    }

    [Test]
    public async Task StartupScan_SkipsFinishedRuns()
    {
        // Detection is O(unfinished candidates): N finished runs are indexed
        // by their terminal marker and skipped — only the unfinished one has
        // its messages read.
        var finished = new List<RunCandidate>();
        for (int i = 0; i < 25; i++)
            finished.Add(new RunCandidate(RunId.New(), $"s-finished-{i}", HasTerminalMarker: true));
        var unfinished = new RunCandidate(RunId.New(), "s-406", HasTerminalMarker: false);
        var all = finished.Append(unfinished).ToList();

        var candidates = UnfinishedRunDetector.SelectCandidates(all);

        await Assert.That(candidates.Count).IsEqualTo(1);
        await Assert.That(candidates[0]).IsEqualTo(unfinished);
    }

    [Test]
    public async Task CleanCancel_WithTerminalEvent_IsNotMisdetected()
    {
        // B2.1 handshake: a clean cancel produces a terminal event, so it
        // must never read as interrupted — cancellation is a decision, not a
        // crash.
        IReadOnlyList<AgentMessage> observed = new AgentMessage[]
        {
            NewUser("s-406", 1),
            NewTextAssistant("s-406", 2),
        };
        var end = new AgentEndEvent(observed, Cancelled: true);

        var outcome = RunOutcome.Reconstruct(RunId.New(), "s-406", observed, end);
        var report = UnfinishedRunDetector.Detect(
            RunId.New(), "s-406", observed,
            hasTerminalEvent: true, isLive: false, hasRunMarker: true,
            isConfirmedByReadBack: _ => true);

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Stopped);
        await Assert.That(report is null).IsTrue();
    }
}
