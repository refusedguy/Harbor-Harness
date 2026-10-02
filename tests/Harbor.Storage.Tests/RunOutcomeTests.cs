using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Storage.Memory;

namespace Harbor.Storage.Tests;

/// <summary>
///     Epic #41 slice B1 (observable run): any finished run reconstructs from
///     the session store — success, error, and stopped cases. The outcome is a
///     read-model over the already-persisted message history (no new table/file);
///     lifecycle arrives via the existing AgentEnd/AgentError events.
/// </summary>
public class RunOutcomeTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static MemorySessionStore CreateStore() => new();

    private static JsonElement JsonArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static UserMessage NewUser(string sessionId, int seq) => new(
        $"u-{seq}-{Guid.NewGuid():N}",
        sessionId,
        BaseTime.AddSeconds(seq),
        "Read README.md",
        "code",
        "kilo-auto");

    private static AssistantMessage NewAssistantWithToolCall(string sessionId, int seq, string toolCallId, StopReason stopReason) => new(
        $"a-{seq}-{Guid.NewGuid():N}",
        sessionId,
        BaseTime.AddSeconds(seq),
        new ContentPart[] { new ToolCallPart(toolCallId, "read", JsonArgs("""{"path":"README.md"}""")) },
        stopReason,
        new Usage(10, 5),
        "kilo-auto");

    private static ToolResultMessage NewToolResult(string sessionId, int seq, string toolCallId, bool isError) => new(
        $"t-{seq}-{Guid.NewGuid():N}",
        sessionId,
        BaseTime.AddSeconds(seq),
        new[] { new ToolResultEntry(toolCallId, "read", isError ? "file not found" : "file contents", isError) });

    private static AssistantMessage NewFinalAssistant(string sessionId, int seq, StopReason stopReason) => new(
        $"f-{seq}-{Guid.NewGuid():N}",
        sessionId,
        BaseTime.AddSeconds(seq),
        new ContentPart[] { new TextPart("Done.") },
        stopReason,
        new Usage(10, 5),
        "kilo-auto");

    [Test]
    public async Task Reconstruct_SuccessfulRunWithToolCall_LinksToolResult()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();
        const string toolCallId = "tc-success-1";

        await store.AppendMessageAsync(sessionId, NewUser(sessionId, 1));
        await store.AppendMessageAsync(sessionId, NewAssistantWithToolCall(sessionId, 2, toolCallId, StopReason.ToolUse));
        await store.AppendMessageAsync(sessionId, NewToolResult(sessionId, 3, toolCallId, isError: false));
        await store.AppendMessageAsync(sessionId, NewFinalAssistant(sessionId, 4, StopReason.Stop));

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value, new AgentEndEvent(stored.Value));

        await Assert.That(outcome.RunId).IsEqualTo(runId);
        await Assert.That(outcome.SessionId).IsEqualTo(sessionId);
        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Succeeded);
        await Assert.That(outcome.ErrorMessage).IsNull();
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(4);
        await Assert.That(outcome.ToolCalls.Count).IsEqualTo(1);
        await Assert.That(outcome.ToolCalls[0].ToolCallId).IsEqualTo(toolCallId);
        await Assert.That(outcome.ToolCalls[0].ToolName).IsEqualTo("read");
        await Assert.That(outcome.ToolCalls[0].IsError).IsFalse();
        await Assert.That(outcome.FinishedAt >= outcome.StartedAt).IsTrue();
    }

    [Test]
    public async Task Reconstruct_ErroredRun_ReportsFailedWithMessage()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();
        const string toolCallId = "tc-error-1";

        await store.AppendMessageAsync(sessionId, NewUser(sessionId, 1));
        await store.AppendMessageAsync(sessionId, NewAssistantWithToolCall(sessionId, 2, toolCallId, StopReason.ToolUse));
        await store.AppendMessageAsync(sessionId, NewToolResult(sessionId, 3, toolCallId, isError: true));

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value,
            new AgentEndEvent(stored.Value),
            new AgentErrorEvent("provider exploded"));

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Failed);
        await Assert.That(outcome.ErrorMessage).IsEqualTo("provider exploded");
        await Assert.That(outcome.ToolCalls.Count).IsEqualTo(1);
        await Assert.That(outcome.ToolCalls[0].IsError).IsTrue();
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Reconstruct_CancelledRun_ReportsStoppedNotFailed()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();

        await store.AppendMessageAsync(sessionId, NewUser(sessionId, 1));
        await store.AppendMessageAsync(sessionId, NewFinalAssistant(sessionId, 2, StopReason.Aborted));

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value,
            new AgentEndEvent(stored.Value, Cancelled: true));

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Stopped);
        await Assert.That(outcome.ErrorMessage).IsNull();
        await Assert.That(outcome.ToolCalls.Count).IsEqualTo(0);
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(2);
    }

    /// <summary>
    ///     Identity-boxing view of a timestamp, used by the #993 boundary tests so
    ///     that one assertion compiles against <em>both</em> the pre-fix
    ///     non-nullable <see cref="DateTimeOffset" /> and the fixed
    ///     <see cref="DateTimeOffset" />?.
    /// </summary>
    /// <remarks>
    ///     Generic on purpose: an overload pair would be dead code (and an
    ///     unused-private-member diagnostic) on whichever side of the fix the file
    ///     is compiled against, whereas this compiles on both. Because the test
    ///     file is byte-identical before and after the fix, any red→green
    ///     difference is attributable to the product source and nothing else.
    /// </remarks>
    private static object? Boxed<T>(T value) => value;

    [Test]
    public async Task Reconstruct_EmptyHistory_ReportsNoEvidenceNotSuccess()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();

        // A session that has never run: no message was ever persisted for it.
        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();
        await Assert.That(stored.Value.Count).IsEqualTo(0);

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value, new AgentEndEvent(stored.Value));

        // #993: the old mapping ended in a catch-all, so this reached
        // `Succeeded` — "the loop exited cleanly" for a loop that never ran.
        await Assert.That(outcome.StopReason).IsNotEqualTo(RunStopReason.Succeeded)
            .Because("an empty history is not evidence of a clean exit");
        await Assert.That(outcome.StopReason).IsNotEqualTo(RunStopReason.Failed)
            .Because("no evidence of work is not a verdict that the work broke");
        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Stopped);

        await Assert.That(outcome.ErrorMessage).IsNull();
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(0);
        await Assert.That(outcome.ToolCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reconstruct_EmptyHistory_DoesNotInventRunTimestamps()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value, new AgentEndEvent(stored.Value));

        // #993: `startedAt = finishedAt = UtcNow` gave a run that never happened a
        // duration of exactly zero, measured at reconstruction time. A plausible
        // fact standing in for a missing one — the #782/#653 class, which this repo
        // has already fixed twice in SessionReadTool ("unknown (model publishes no
        // price)", "(empty transcript)"). Answering "unknown" beats answering wrongly.
        await Assert.That(Boxed(outcome.StartedAt)).IsNull()
            .Because("no message exists to date the start of a run that never ran");
        await Assert.That(Boxed(outcome.FinishedAt)).IsNull()
            .Because("no message exists to date the finish of a run that never ran");
    }

    [Test]
    public async Task Reconstruct_EmptyHistory_KeepsACancellationVerdictButStillUnknownTime()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value, new AgentEndEvent(stored.Value, Cancelled: true));

        // Direction check #1: a known terminal signal must still win. Cancelled
        // before the first message was persisted is a real, reportable stop — the
        // fix must not swallow it into "unknown".
        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Stopped);
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(0);

        // …and the timestamps are still unknown, because cancellation is not a
        // clock reading. Pre-fix this pair was a fabricated UtcNow.
        await Assert.That(Boxed(outcome.StartedAt)).IsNull();
        await Assert.That(Boxed(outcome.FinishedAt)).IsNull();
    }

    [Test]
    public async Task Reconstruct_EmptyHistory_StillReportsAKnownErrorAsFailed()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value,
            new AgentEndEvent(stored.Value),
            new AgentErrorEvent("provider exploded before anything was persisted"));

        // Direction check #2 — the decisive one. If "empty history" had been
        // mapped to a blanket "no evidence, stop asserting anything", the known
        // failure would have been lost. An error the caller told us about is
        // evidence; the fix must be scoped to evidence *we* lack.
        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Failed);
        await Assert.That(outcome.ErrorMessage).IsEqualTo("provider exploded before anything was persisted");
    }

    [Test]
    public async Task Reconstruct_HistoryWithNoAssistantMessage_ReportsNoEvidenceAndKeepsRealTime()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;
        var runId = RunId.New();

        // A prompt was persisted, but the loop never produced a reply: the same
        // "no assistant evidence" state as an empty history, reached the way a
        // live run reaches it. This is the case the live sibling
        // SessionSupervision.InferOutcome already answers "unknown".
        await store.AppendMessageAsync(sessionId, NewUser(sessionId, 1));

        var stored = await store.GetMessagesAsync(sessionId);
        await Assert.That(stored.IsSuccess).IsTrue();

        var outcome = RunOutcome.Reconstruct(
            runId, sessionId, stored.Value, new AgentEndEvent(stored.Value));

        await Assert.That(outcome.StopReason).IsNotEqualTo(RunStopReason.Succeeded)
            .Because("no assistant message means no evidence the loop exited cleanly");
        await Assert.That(outcome.MessageIds.Count).IsEqualTo(1);

        // The timestamp is NOT unknown here — a message exists to date it, and the
        // fix must keep it exact rather than generalising "unknown" to every run.
        // Boxed so the assertion binds identically on either side of the fix.
        await Assert.That(Boxed(outcome.StartedAt)).IsEqualTo(BaseTime.AddSeconds(1));
        await Assert.That(Boxed(outcome.FinishedAt)).IsEqualTo(BaseTime.AddSeconds(1));
    }

    [Test]
    public async Task Reconstruct_SuccessAndNoEvidence_DisagreeOnTheSameCodePath()
    {
        // Non-vacuity, in the form that matters here: the two outcomes come from the
        // SAME mapping and differ only in whether a run left evidence behind. If the
        // empty-history test could not tell them apart, it would be asserting a
        // constant rather than a boundary — the #591 "half a rule, plausible zero"
        // failure mode. A completed run and a run that never happened must land on
        // opposite verdicts here, and both must be reachable.
        var store = CreateStore();
        var session = await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto");
        await Assert.That(session.IsSuccess).IsTrue();

        string sessionId = session.Value.Id;

        var empty = await store.GetMessagesAsync(sessionId);
        await Assert.That(empty.IsSuccess).IsTrue();
        var noEvidence = RunOutcome.Reconstruct(
            RunId.New(), sessionId, empty.Value, new AgentEndEvent(empty.Value));

        await store.AppendMessageAsync(sessionId, NewUser(sessionId, 1));
        await store.AppendMessageAsync(sessionId, NewFinalAssistant(sessionId, 2, StopReason.Stop));

        var ran = await store.GetMessagesAsync(sessionId);
        await Assert.That(ran.IsSuccess).IsTrue();
        var succeeded = RunOutcome.Reconstruct(
            RunId.New(), sessionId, ran.Value, new AgentEndEvent(ran.Value));

        await Assert.That(succeeded.StopReason).IsEqualTo(RunStopReason.Succeeded);
        await Assert.That(noEvidence.StopReason).IsNotEqualTo(RunStopReason.Succeeded);
        await Assert.That(noEvidence.StopReason).IsNotEqualTo(succeeded.StopReason);
    }
}
