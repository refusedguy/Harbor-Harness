// CompactionEventStoreTests.cs — the compaction-record seam of the session
// stores (#1767).
//
// The compaction lock is only as good as the record behind it, and the record
// is only as good as the backend that keeps it. These tests hold all three
// in-tree backends to the same contract:
//
//   * a start record is written and read back;
//   * an unmatched start HOLDS the lock — the crash case;
//   * an end record releases it, and carries the failure text;
//   * a session that never compacted yields an empty list, not a failure;
//   * a session that does not exist fails with the shared "not found" text.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Storage.Tests;

/// <summary>
///     The compaction-record seam, held to one contract by every backend
///     (#1767).
/// </summary>
public class CompactionEventStoreTests
{
    private static SessionCompactionEvent StartRecord(int turn)
    {
        var record = new SessionCompactionEvent
        {
            Kind = SessionCompactionEventKind.Start,
            Turn = turn,
            OccurredAt = DateTimeOffset.UnixEpoch,
        };

        return record;
    }

    private static SessionCompactionEvent EndRecord(int turn, string? error)
    {
        var record = new SessionCompactionEvent
        {
            Kind = SessionCompactionEventKind.End,
            Turn = turn,
            Error = error,
            PrunedMessageCount = 7,
            TokensSaved = 123,
            OccurredAt = DateTimeOffset.UnixEpoch,
        };

        return record;
    }

    /// <summary>
    ///     The contract every backend answers to: the record round-trips, the
    ///     lock is held by an unmatched start and released by an end, and the
    ///     failure text survives.
    /// </summary>
    private static async Task AssertTheContractAsync(ICompactionEventStore store, string sessionId)
    {
        Result<IReadOnlyList<SessionCompactionEvent>> empty = await store.GetCompactionEventsAsync(sessionId);
        await Assert.That(empty.IsSuccess).IsTrue();
        await Assert.That(empty.Value).IsEmpty();

        Result appended = await store.AppendCompactionEventAsync(sessionId, StartRecord(3));
        await Assert.That(appended.IsSuccess).IsTrue();

        Result<IReadOnlyList<SessionCompactionEvent>> afterStart = await store.GetCompactionEventsAsync(sessionId);
        await Assert.That(afterStart.Value.Count).IsEqualTo(1);
        await Assert.That(afterStart.Value[0].Kind).IsEqualTo(SessionCompactionEventKind.Start);
        await Assert.That(afterStart.Value[0].Turn).IsEqualTo(3);

        // The unmatched start HOLDS the lock: this is the shape a crashed
        // process leaves behind.
        SessionCompactionEvent? open = CompactionLock.FindOpenStart(afterStart.Value);
        await Assert.That(open).IsNotNull();
        await Assert.That(open!.Turn).IsEqualTo(3);

        Result ended = await store.AppendCompactionEventAsync(sessionId, EndRecord(3, "the summarizer exploded"));
        await Assert.That(ended.IsSuccess).IsTrue();

        Result<IReadOnlyList<SessionCompactionEvent>> afterEnd = await store.GetCompactionEventsAsync(sessionId);
        await Assert.That(afterEnd.Value.Count).IsEqualTo(2);
        await Assert.That(afterEnd.Value[1].Kind).IsEqualTo(SessionCompactionEventKind.End);
        await Assert.That(afterEnd.Value[1].Error).IsEqualTo("the summarizer exploded");
        await Assert.That(afterEnd.Value[1].PrunedMessageCount).IsEqualTo(7);
        await Assert.That(afterEnd.Value[1].TokensSaved).IsEqualTo(123);
        await Assert.That(CompactionLock.FindOpenStart(afterEnd.Value)).IsNull();
    }

    /// <summary>
    ///     A session that does not exist fails with the shared text — the
    ///     same sentence every backend has always used.
    /// </summary>
    private static async Task AssertTheMissingSessionFailsAsync(ICompactionEventStore store)
    {
        Result missing = await store.AppendCompactionEventAsync("no-such-session", StartRecord(1));
        await Assert.That(missing.IsFailure).IsTrue();
        await Assert.That(missing.Error).IsEqualTo("Session 'no-such-session' not found.");

        Result<IReadOnlyList<SessionCompactionEvent>> read = await store.GetCompactionEventsAsync("no-such-session");
        await Assert.That(read.IsFailure).IsTrue();
        await Assert.That(read.Error).IsEqualTo("Session 'no-such-session' not found.");
    }

    [Test]
    public async Task The_Memory_Store_Holds_The_Compaction_Contract()
    {
        var store = new MemorySessionStore();
        var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;

        await AssertTheContractAsync(store, session.Id);
        await AssertTheMissingSessionFailsAsync(store);
    }

    [Test]
    public async Task The_Jsonl_Store_Holds_The_Compaction_Contract()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-compaction-jsonl-{Guid.NewGuid():N}");
        var store = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;

            await AssertTheContractAsync(store, session.Id);
            await AssertTheMissingSessionFailsAsync(store);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task The_Sqlite_Store_Holds_The_Compaction_Contract()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"harbor-compaction-sqlite-{Guid.NewGuid():N}.db");
        var store = new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;

            await AssertTheContractAsync(store, session.Id);
            await AssertTheMissingSessionFailsAsync(store);
        }
        finally
        {
            // The pooled connections keep the database file open, and Windows
            // refuses to delete a file another process holds. Releasing the
            // pools first is the pattern the sibling sqlite suites use.
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
