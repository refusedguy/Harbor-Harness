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
}
