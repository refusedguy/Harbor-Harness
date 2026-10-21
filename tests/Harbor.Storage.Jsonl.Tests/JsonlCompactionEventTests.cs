// JsonlCompactionEventTests.cs — the compaction records in the JSONL session
// file (#1767).
//
// Three facts the JSONL backend owes the product:
//
//   1. the compaction record is a LINE of the session file, beside the
//      messages — and the message read path does not mistake it for a
//      message (it is not one, and it is not a broken record either);
//   2. the record SURVIVES the process: a second store over the same
//      directory reads the lock back, which is the whole point of a durable
//      lock;
//   3. the keyless tier scrubs the failure text on the way out (#1740) —
//      the reason a compaction failed can quote a provider error that quotes
//      a credential.

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The compaction records of the JSONL session file (#1767).
/// </summary>
public class JsonlCompactionEventTests
{
    private const string OpaqueKey = "provider-secret-value-4242";

    private static JsonlSessionStore CreateStore(SessionLogTier tier, params string[] literalSecrets)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance, tier, literalSecrets);
    }

    private static SessionCompactionEvent StartRecord(int turn, DateTimeOffset occurredAt)
    {
        var record = new SessionCompactionEvent
        {
            Kind = SessionCompactionEventKind.Start,
            Turn = turn,
            OccurredAt = occurredAt,
        };

        return record;
    }

    [Test]
    public async Task The_Compaction_Record_Is_A_Line_Of_The_Session_File_And_Not_A_Message()
    {
        var store = CreateStore(SessionLogTier.Local);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

            var user = new UserMessage(
                "msg-user", session.Id, DateTimeOffset.UtcNow, "hello", "code", "claude-opus-4");
            await store.AppendMessageAsync(session.Id, user);
            await store.AppendCompactionEventAsync(session.Id, StartRecord(1, DateTimeOffset.UtcNow));

            // The record is IN the file, beside the messages.
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            string raw = await File.ReadAllTextAsync(path);
            await Assert.That(raw).Contains("\"type\":\"compaction\"");

            // And it is not a message: the history the model context is built
            // from carries the message and nothing else — and the record is
            // not counted as a broken record either.
            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages).IsSuccessful();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(messages.Value[0].Id).IsEqualTo("msg-user");

            var transcript = await store.GetTranscriptAsync(session.Id);
            await Assert.That(transcript).IsSuccessful();
            await Assert.That(transcript.Value.Integrity.UnreadableRecordCount).IsEqualTo(0);
            await Assert.That(transcript.Value.Integrity.IsComplete).IsTrue();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
            {
                Directory.Delete(store.GetRootDirectory(), true);
            }
        }
    }

    [Test]
    public async Task The_Compaction_Record_Survives_The_Process()
    {
        var store = CreateStore(SessionLogTier.Local);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendCompactionEventAsync(session.Id, StartRecord(5, DateTimeOffset.UtcNow));

            // A second store over the same directory is a new process reading
            // the same log — and the lock is still there.
            var reopened = new JsonlSessionStore(
                store.GetRootDirectory(), NullLogger<JsonlSessionStore>.Instance);

            var events = await reopened.GetCompactionEventsAsync(session.Id);
            await Assert.That(events).IsSuccessful();
            await Assert.That(events.Value.Count).IsEqualTo(1);
            await Assert.That(events.Value[0].Turn).IsEqualTo(5);

            SessionCompactionEvent? open = CompactionLock.FindOpenStart(events.Value);
            await Assert.That(open).IsNotNull();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
            {
                Directory.Delete(store.GetRootDirectory(), true);
            }
        }
    }

    [Test]
    public async Task The_Keyless_Tier_Scrubs_The_Failure_Text()
    {
        var store = CreateStore(SessionLogTier.Keyless, OpaqueKey);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

            var failed = new SessionCompactionEvent
            {
                Kind = SessionCompactionEventKind.End,
                Turn = 1,
                Error = $"the provider rejected the key {OpaqueKey}",
                OccurredAt = DateTimeOffset.UtcNow,
            };
            await store.AppendCompactionEventAsync(session.Id, failed);

            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            string raw = await File.ReadAllTextAsync(path);

            await Assert.That(raw).DoesNotContain(OpaqueKey);
            await Assert.That(raw).Contains(SecretRedactor.SecretPlaceholder);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
            {
                Directory.Delete(store.GetRootDirectory(), true);
            }
        }
    }
}
