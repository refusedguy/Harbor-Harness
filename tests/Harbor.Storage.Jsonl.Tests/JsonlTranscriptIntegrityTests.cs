using System.Text.Json;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #1744 — a cold read of a session must survive a record the writer was
///     killed in the middle of, and must not drop that record silently.
/// </summary>
/// <remarks>
///     <para>
///         The four shapes a session file can be read in are pinned here: an
///         empty file, a file whose LAST record was cut in half, a file whose
///         MIDDLE record was cut in half, and a whole file. The first three
///         are what a killed process leaves behind; the fourth is the
///         regression that says the read did not start reporting.
///     </para>
///     <para>
///         The policy the tests pin is the honest one: the read NEVER fails on
///         a broken record, and it NEVER drops one without saying so. A record
///         that was cut in half has no recoverable content — there is nothing
///         to salvage from half a JSON document — so it is skipped, but the
///         skip is counted and the tail case is named. Silently dropping it
///         would make the artifact claim "the agent never said that", which is
///         the failure this issue exists to remove.
///     </para>
/// </remarks>
public class JsonlTranscriptIntegrityTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int second) => Epoch.AddSeconds(second);

    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    /// <summary>One user message, encoded exactly as the store would write it.</summary>
    private static byte[] UserLine(string sessionId, string id, int second, string content)
    {
        var message = TestMessages.User(content, id: id, sessionId: sessionId, createdAt: At(second));
        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
        return SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
    }

    /// <summary>
    ///     One assistant message that asks for a tool — and, in the shape this
    ///     test builds, never gets an answer.
    /// </summary>
    private static byte[] AssistantToolCallLine(string sessionId, string id, int second, string callId, string toolName)
    {
        using var args = JsonDocument.Parse("{}");
        var message = TestMessages.AssistantParts([new ToolCallPart(callId, toolName, args.RootElement.Clone())], id: id, sessionId: sessionId, createdAt: At(second), stopReason: StopReason.ToolUse, usage: new Usage(1, 1));
        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
        return SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
    }

    private static void Drop(JsonlSessionStore store)
    {
        if (Directory.Exists(store.GetRootDirectory()))
            Directory.Delete(store.GetRootDirectory(), true);
    }

    /// <summary>
    ///     Append bytes to the session file behind the store's back — the
    ///     whole point of these tests is to write a file the store never
    ///     wrote.
    /// </summary>
    private static async Task AppendAsync(string path, byte[] bytes)
    {
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
        await fs.WriteAsync(bytes.AsMemory());
    }

    /// <summary>
    ///     The empty file: nothing to read, and nothing to report. A session
    ///     whose file was truncated to zero bytes (a crash before the header
    ///     was flushed) must still answer a read.
    /// </summary>
    [Test]
    public async Task EmptyFile_ReadsAsAnEmptyTranscript_WithNothingLost()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            File.WriteAllText(path, string.Empty);

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript).IsSuccessful();
            await Assert.That(transcript.Value.Messages).IsEmpty();
            await Assert.That(transcript.Value.Integrity.UnreadableRecordCount).IsEqualTo(0);
            await Assert.That(transcript.Value.Integrity.IsTailTruncated).IsFalse();
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls).IsEmpty();
            await Assert.That(transcript.Value.Integrity.IsComplete).IsTrue();
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>
    ///     The cut tail: the write was interrupted in the middle of the last
    ///     record. The read delivers what is whole and REPORTS the cut one —
    ///     the shape a process killed with SIGKILL leaves behind.
    /// </summary>
    [Test]
    public async Task TruncatedLastLine_ReadsTheWholeRecords_AndReportsTheTailAsTruncated()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");

            byte[] whole = UserLine(session.Id, "m1", 1, "kept");
            byte[] cut = UserLine(session.Id, "m2", 2, "cut short");
            await AppendAsync(path, whole);
            await AppendAsync(path, cut.AsMemory(0, cut.Length / 2).ToArray());

            var transcript = await store.GetTranscriptAsync(session.Id);

            // The read does NOT fail on the cut record.
            await Assert.That(transcript).IsSuccessful();
            // The whole record is delivered; the cut one is not invented.
            await Assert.That(transcript.Value.Messages.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)transcript.Value.Messages[0]).Content).IsEqualTo("kept");
            // ... and it is REPORTED, not dropped in silence.
            await Assert.That(transcript.Value.Integrity.UnreadableRecordCount).IsEqualTo(1);
            await Assert.That(transcript.Value.Integrity.IsTailTruncated).IsTrue();
            await Assert.That(transcript.Value.Integrity.IsComplete).IsFalse();
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>
    ///     The cut middle: a record in the middle of the log does not decode.
    ///     The tail of the log is intact, so the read says so — "the tail was
    ///     cut" and "a record in the middle is corrupt" are different facts and
    ///     call for different repairs.
    /// </summary>
    [Test]
    public async Task TruncatedMiddleLine_ReadsTheWholeRecords_AndDoesNotClaimTheTailIsCut()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");

            byte[] first = UserLine(session.Id, "m1", 1, "first");
            byte[] cut = UserLine(session.Id, "m2", 2, "cut short");
            byte[] last = UserLine(session.Id, "m3", 3, "last");
            await AppendAsync(path, first);
            await AppendAsync(path, cut.AsMemory(0, cut.Length / 2).ToArray());
            await AppendAsync(path, "\n"u8.ToArray());
            await AppendAsync(path, last);

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript).IsSuccessful();
            // Both whole records survive; the broken one in the middle does not
            // take the rest of the log with it.
            await Assert.That(transcript.Value.Messages.Count).IsEqualTo(2);
            await Assert.That(((UserMessage)transcript.Value.Messages[0]).Content).IsEqualTo("first");
            await Assert.That(((UserMessage)transcript.Value.Messages[1]).Content).IsEqualTo("last");
            // The broken record is counted...
            await Assert.That(transcript.Value.Integrity.UnreadableRecordCount).IsEqualTo(1);
            // ... and the tail, which is whole, is not reported as cut.
            await Assert.That(transcript.Value.Integrity.IsTailTruncated).IsFalse();
            await Assert.That(transcript.Value.Integrity.IsComplete).IsFalse();
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>
    ///     The regression: a whole file reads as a whole transcript, with
    ///     nothing reported. The reporting must not turn every healthy session
    ///     into a damaged one.
    /// </summary>
    [Test]
    public async Task WholeFile_ReadsAsACompleteTranscript_WithNothingReported()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id,
                TestMessages.User("hello", id: "m1", sessionId: session.Id, createdAt: At(1)));
            await store.AppendMessageAsync(session.Id,
                TestMessages.User("again", id: "m2", sessionId: session.Id, createdAt: At(2)));

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript).IsSuccessful();
            await Assert.That(transcript.Value.Messages.Count).IsEqualTo(2);
            await Assert.That(transcript.Value.Integrity.UnreadableRecordCount).IsEqualTo(0);
            await Assert.That(transcript.Value.Integrity.IsTailTruncated).IsFalse();
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls).IsEmpty();
            await Assert.That(transcript.Value.Integrity.IsComplete).IsTrue();
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>
    ///     The half-turn: the assistant asked for a tool and the results never
    ///     landed. The read names the call, closes the turn IN MEMORY so the
    ///     transcript a reader gets is balanced, and writes nothing back.
    /// </summary>
    [Test]
    public async Task UnclosedToolCall_IsNamed_AndClosedInMemory_WithoutTouchingTheFile()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            await AppendAsync(path, UserLine(session.Id, "m1", 1, "read the file"));
            await AppendAsync(path, AssistantToolCallLine(session.Id, "m2", 2, "call-1", "read"));

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript).IsSuccessful();
            // The call is NAMED — the transcript is not silently "complete".
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls.Count).IsEqualTo(1);
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls[0].Id).IsEqualTo("call-1");
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls[0].ToolName).IsEqualTo("read");
            await Assert.That(transcript.Value.Integrity.IsComplete).IsFalse();

            // The transcript a reader gets is CLOSED: the call has an answer
            // again, carrying the outcome-unknown code.
            var closers = transcript.Value.Messages.OfType<ToolResultMessage>().ToList();
            await Assert.That(closers.Count).IsEqualTo(1);
            await Assert.That(closers[0].Results.Count).IsEqualTo(1);
            await Assert.That(closers[0].Results[0].ToolCallId).IsEqualTo("call-1");
            await Assert.That(closers[0].Results[0]).HasFailed();
            await Assert.That(closers[0].Results[0].Output).Contains(UnresolvedToolCall.OutcomeUnknownCode);

            // Nothing was written back: the LOG still holds the two records
            // and no closer.
            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages).IsSuccessful();
            await Assert.That(messages.Value.Count).IsEqualTo(2);
            await Assert.That(messages.Value.OfType<ToolResultMessage>()).IsEmpty();
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>
    ///     A tool call that WAS answered is not reported and is not closed a
    ///     second time — a duplicated result is as invalid for a provider as a
    ///     missing one.
    /// </summary>
    [Test]
    public async Task AnsweredToolCall_IsNotReported_AndIsNotClosedTwice()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id,
                TestMessages.User("read the file", id: "m1", sessionId: session.Id, createdAt: At(1)));
            await store.AppendMessageAsync(session.Id, TestMessages.AssistantParts([new ToolCallPart("call-1", "read", JsonDocument.Parse("{}").RootElement.Clone())], id: "m2", sessionId: session.Id, createdAt: At(2), stopReason: StopReason.ToolUse, usage: new Usage(1, 1)));
            await store.AppendMessageAsync(session.Id, TestMessages.ToolResult([new ToolResultEntry("call-1", "read", "file contents", false)], id: "m3", sessionId: session.Id, createdAt: At(3)));

            var transcript = await store.GetTranscriptAsync(session.Id);

            await Assert.That(transcript).IsSuccessful();
            await Assert.That(transcript.Value.Integrity.UnclosedToolCalls).IsEmpty();
            await Assert.That(transcript.Value.Integrity.IsComplete).IsTrue();
            await Assert.That(transcript.Value.Messages.OfType<ToolResultMessage>().Count()).IsEqualTo(1);
        }
        finally
        {
            Drop(store);
        }
    }
}
