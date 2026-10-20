using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

/// <summary>
///     #1744 — the transcript read is a CONTRACT of a session store, not a
///     JSONL detail: every backend answers it the same way.
/// </summary>
/// <remarks>
///     <para>
///         The fact under test is the one a crashed process leaves behind: the
///         assistant asked for a tool, the results never landed, and the
///         transcript says so instead of reading as a finished conversation. The
///         backend differences that do NOT show up here are the point of the
///         parity: a SQLite row cannot be half-written the way a JSONL record
///         can, but a half-turn can happen in either — and the read names it
///         the same way.
///     </para>
/// </remarks>
[ParallelLimiter<SqliteStoreLimit>]
public class StorageTranscriptIntegrityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static SqliteSessionStore CreateSqlite(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"harbor-sqlite-tail-{Guid.NewGuid():N}.db");
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static MemorySessionStore CreateMemory() =>
        new();

    private static ToolCallPart Call(string id, string toolName) =>
        new(id, toolName, JsonDocument.Parse("{}").RootElement.Clone());

    private static UserMessage User(string id, string sessionId) =>
        new(id, sessionId, T0, "read the file", "code", "claude-opus-4");

    private static AssistantMessage Asking(string id, string sessionId) =>
        new(
            id,
            sessionId,
            T0,
            [Call("call-1", "read")],
            StopReason.ToolUse,
            new Usage(1, 1),
            "claude-opus-4");

    /// <summary>
    ///     SQLite: the unclosed call is named, and the transcript a reader gets
    ///     is closed in memory — while the log keeps its two records.
    /// </summary>
    [Test]
    public async Task Sqlite_UnclosedToolCall_IsNamedAndClosedInMemory_AndTheLogIsUntouched()
    {
        var store = CreateSqlite(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, User("m1", session.Id));
            await store.AppendMessageAsync(session.Id, Asking("m2", session.Id));

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript.IsSuccess).IsTrue();
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls.Count).IsEqualTo(1);
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls[0].Id).IsEqualTo("call-1");
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls[0].ToolName).IsEqualTo("read");
            await Assert.That(transcript.Value.Integrity.IsComplete).IsFalse();

            var closers = transcript.Value.Messages.OfType<ToolResultMessage>().ToList();
            await Assert.That(closers.Count).IsEqualTo(1);
            await Assert.That(closers[0].Results[0].ToolCallId).IsEqualTo("call-1");
            await Assert.That(closers[0].Results[0].IsError).IsTrue();
            await Assert.That(closers[0].Results[0].Output).Contains(UnresolvedToolCall.OutcomeUnknownCode);

            // The log is untouched: the closer lives in memory only.
            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(2);
            await Assert.That(messages.Value.OfType<ToolResultMessage>()).IsEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    ///     The memory backend answers the same way: an in-memory store has no
    ///     unreadable records, but it can hold the same half-turn.
    /// </summary>
    [Test]
    public async Task Memory_UnclosedToolCall_IsNamedAndClosedInMemory()
    {
        var store = CreateMemory();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        await store.AppendMessageAsync(session.Id, User("m1", session.Id));
        await store.AppendMessageAsync(session.Id, Asking("m2", session.Id));

        var transcript = await store.GetTranscriptAsync(session.Id);

        await Assert.That(transcript.IsSuccess).IsTrue();
        await Assert.That(transcript.Value.Integrity.UnreadableRecordCount).IsEqualTo(0);
        await Assert.That(transcript.Value.Integrity.IsTailTruncated).IsFalse();
        await Assert.That(transcript.Value.Integrity.UnclosedToolCalls.Count).IsEqualTo(1);
        await Assert.That(transcript.Value.Integrity.UnclosedToolCalls[0].ToolName).IsEqualTo("read");

        var closers = transcript.Value.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(closers.Count).IsEqualTo(1);
        await Assert.That(closers[0].Results[0].Output).Contains(UnresolvedToolCall.OutcomeUnknownCode);
    }

    /// <summary>
    ///     The regression for both backends: a finished conversation reads as
    ///     a complete transcript, with nothing reported and nothing added.
    /// </summary>
    [Test]
    public async Task Sqlite_AWholeTranscript_ReadsAsComplete()
    {
        var store = CreateSqlite(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, User("m1", session.Id));
            await store.AppendMessageAsync(session.Id, Asking("m2", session.Id));
            await store.AppendMessageAsync(session.Id, new ToolResultMessage(
                "m3",
                session.Id,
                T0,
                [new ToolResultEntry("call-1", "read", "file contents", false)]));

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript.IsSuccess).IsTrue();
            await Assert.That(transcript.Value.Integrity.IsComplete).IsTrue();
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls).IsEmpty();
            await Assert.That(transcript.Value.Messages.Count).IsEqualTo(3);
            await Assert.That(transcript.Value.Messages.OfType<ToolResultMessage>().Count()).IsEqualTo(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
