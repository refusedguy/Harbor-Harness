// OperationMarkerStoreTests.cs — the fork/turn crash-marker seam (#2226),
// held to one contract by every backend the way the compaction seam is.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.TestKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Storage.Tests;

[ParallelLimiter<SqliteStoreLimit>]
public class OperationMarkerStoreTests
{
    private static SessionOperationEvent Start(string owner) => new()
    {
        Kind = SessionOperationKind.ForkStart,
        Owner = owner,
        BoundaryMessageId = "m1",
        Copied = 1,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    private static SessionOperationEvent End(string owner) => new()
    {
        Kind = SessionOperationKind.ForkEnd,
        Owner = owner,
        Copied = 1,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task AssertTheContractAsync(ISessionOperationEventStore store, string sessionId)
    {
        IReadOnlyList<SessionOperationEvent> empty = (await store.GetOperationEventsAsync(sessionId)).Value;
        await Assert.That(empty).IsEmpty();

        await store.AppendOperationEventAsync(sessionId, Start("fork-1"));
        IReadOnlyList<SessionOperationEvent> open = (await store.GetOperationEventsAsync(sessionId)).Value;
        await Assert.That(open.Count).IsEqualTo(1);
        await Assert.That(SessionOperationMarkers.HasIncompleteFork(open)).IsTrue();
        await Assert.That(open[0].Sequence).IsEqualTo(0);
        await Assert.That(open[0].BoundaryMessageId).IsEqualTo("m1");

        await store.AppendOperationEventAsync(sessionId, End("fork-1"));
        IReadOnlyList<SessionOperationEvent> closed = (await store.GetOperationEventsAsync(sessionId)).Value;
        await Assert.That(closed.Count).IsEqualTo(2);
        await Assert.That(closed[1].Sequence).IsEqualTo(1);
        await Assert.That(SessionOperationMarkers.HasIncompleteFork(closed)).IsFalse();
    }

    private static async Task AssertMissingSessionFailsAsync(ISessionOperationEventStore store)
    {
        Result appended = await store.AppendOperationEventAsync("no-such-session", Start("fork-1"));
        await Assert.That(appended.IsFailure).IsTrue();
        await Assert.That(appended.Error).IsEqualTo("Session 'no-such-session' not found.");

        Result<IReadOnlyList<SessionOperationEvent>> read = await store.GetOperationEventsAsync("no-such-session");
        await Assert.That(read.IsFailure).IsTrue();
        await Assert.That(read.Error).IsEqualTo("Session 'no-such-session' not found.");
    }

    [Test]
    public async Task The_Memory_Store_Holds_The_Operation_Marker_Contract()
    {
        var store = new MemorySessionStore();
        var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
        await AssertTheContractAsync(store, session.Id);
        await AssertMissingSessionFailsAsync(store);
    }

    [Test]
    public async Task The_Jsonl_Store_Holds_The_Operation_Marker_Contract()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-ops-jsonl-{Guid.NewGuid():N}");
        var store = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            await AssertTheContractAsync(store, session.Id);
            await AssertMissingSessionFailsAsync(store);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task The_Sqlite_Store_Holds_The_Operation_Marker_Contract()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"harbor-ops-sqlite-{Guid.NewGuid():N}.db");
        var store = new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            await AssertTheContractAsync(store, session.Id);
            await AssertMissingSessionFailsAsync(store);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SqliteBatch_FailsAtomically_WhenOneRowViolatesThePrimaryKey()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"harbor-ops-batch-{Guid.NewGuid():N}.db");
        var store = new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var first = new UserMessage("dup-id", session.Id, DateTimeOffset.UtcNow, "first", "code", "test-model");
            var duplicate = new UserMessage("dup-id", session.Id, DateTimeOffset.UtcNow, "second", "code", "test-model");

            Result result = await store.AppendMessagesBatchAsync(session.Id, [first, duplicate]);

            await Assert.That(result.IsFailure).IsTrue();
            IReadOnlyList<AgentMessage> messages = (await store.GetMessagesAsync(session.Id)).Value;
            await Assert.That(messages).IsEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
