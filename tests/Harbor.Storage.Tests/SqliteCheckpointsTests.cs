using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Harbor.TestKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

/// <summary>
///     Slice-2 tests for #1247 (checkpoints/rewind on the sqlite backend):
///     the same observable contract the jsonl slice pinned — a checkpoint
///     records the anchor's message index and stays invisible to history
///     reads, a rewind truncates to the anchor and leaves an honest trail
///     marker, later checkpoints go with the dropped future, unknown ids
///     fail without touching the session — plus the reload round-trip: the
///     checkpoint table and the truncated history survive a fresh store
///     instance over the same database file.
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteCheckpointsTests
{
    private static SqliteSessionStore Create(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"harbor-sqlite-ckpt-{Guid.NewGuid():N}.db");
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static void Cleanup(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    private static UserMessage Msg(string sessionId, int i) => new(
        $"msg-{i:D2}-{Guid.NewGuid():N}",
        sessionId,
        DateTimeOffset.UtcNow.AddMilliseconds(i * 5), // distinct stamps: stable ordering
        $"message {i}",
        "code",
        "test-model");

    private static async Task<List<string>> SeedAsync(SqliteSessionStore store, string sessionId, int count)
    {
        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var message = Msg(sessionId, i);
            await store.AppendMessageAsync(sessionId, message);
            ids.Add(message.Id);
        }

        return ids;
    }

    [Test]
    public async Task CreateCheckpoint_RecordsIndex_AndLeavesHistoryUntouched()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 3);

            var marked = await store.CreateCheckpointAsync(session.Id, ids[1], "before-refactor");
            await Assert.That(marked).IsSuccessful();
            await Assert.That(marked.Value.MessageId).IsEqualTo(ids[1]);
            await Assert.That(marked.Value.MessageIndex).IsEqualTo(1);
            await Assert.That(marked.Value.Label).IsEqualTo("before-refactor");
            await Assert.That(marked.Value.IsRewindTrail).IsFalse();

            // Checkpoints are metadata: history reads see exactly the messages.
            var reread = await store.GetMessagesAsync(session.Id);
            await Assert.That(reread.Value.Count).IsEqualTo(3);
            for (int i = 0; i < ids.Count; i++)
                await Assert.That(reread.Value[i].Id).IsEqualTo(ids[i]);

            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(1);
            await Assert.That(listed.Value[0].Id).IsEqualTo(marked.Value.Id);
            await Assert.That(listed.Value[0].MessageIndex).IsEqualTo(1);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task RewindToCheckpoint_Truncates_AndLeavesHonestTrail()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 4);
            var checkpoint = (await store.CreateCheckpointAsync(session.Id, ids[1])).Value;

            var rewound = await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(rewound).IsSuccessful();
            await Assert.That(rewound.Value.Removed).IsEqualTo(2);
            await Assert.That(rewound.Value.Remaining).IsEqualTo(2);
            await Assert.That(rewound.Value.MessageId).IsEqualTo(ids[1]);

            var reread = await store.GetMessagesAsync(session.Id);
            await Assert.That(reread.Value.Count).IsEqualTo(2);
            await Assert.That(reread.Value[0].Id).IsEqualTo(ids[0]);
            await Assert.That(reread.Value[1].Id).IsEqualTo(ids[1]);

            // The honest trail: the checkpoint survives, and a trail marker
            // records the rewind — without leaking into message history.
            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(2);
            await Assert.That(listed.Value[0].Id).IsEqualTo(checkpoint.Id);
            await Assert.That(listed.Value[1].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value[1].RewindOf).IsEqualTo(checkpoint.Id);
            await Assert.That(listed.Value[1].Removed).IsEqualTo(2);

            // Appending after a rewind works, and re-anchoring is honest.
            await store.AppendMessageAsync(session.Id, Msg(session.Id, 99));
            var after = await store.GetMessagesAsync(session.Id);
            await Assert.That(after.Value.Count).IsEqualTo(3);

            // The restored checkpoint survived, so rewinding there twice works.
            var rewoundAgain = await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(rewoundAgain).IsSuccessful();
            await Assert.That(rewoundAgain.Value.Removed).IsEqualTo(1);
            await Assert.That(rewoundAgain.Value.Remaining).IsEqualTo(2);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task RewindToCheckpoint_DropsLaterCheckpoints_KeepsEarlierOnes()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 4);
            var early = (await store.CreateCheckpointAsync(session.Id, ids[0], "early")).Value;
            var late = await store.CreateCheckpointAsync(session.Id, ids[2], "late");

            var rewound = await store.RewindToCheckpointAsync(session.Id, early.Id);
            await Assert.That(rewound).IsSuccessful();
            await Assert.That(rewound.Value.Removed).IsEqualTo(3);
            await Assert.That(rewound.Value.Remaining).IsEqualTo(1);

            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(2);
            await Assert.That(listed.Value[0].Id).IsEqualTo(early.Id);
            await Assert.That(listed.Value[1].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value.Any(c => c.Id == late.Value.Id)).IsFalse();

            // The dropped future's messages are gone, the kept one intact.
            var reread = await store.GetMessagesAsync(session.Id);
            await Assert.That(reread.Value.Count).IsEqualTo(1);
            await Assert.That(reread.Value[0].Id).IsEqualTo(ids[0]);
            await Assert.That(((UserMessage)reread.Value[0]).Content).IsEqualTo("message 0");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task Checkpoint_And_Rewind_RoundTrip_Through_Reload()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 4);
            var checkpoint = (await store.CreateCheckpointAsync(session.Id, ids[1], "label")).Value;
            var rewound = await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(rewound).IsSuccessful();

            var listedBefore = await store.ListCheckpointsAsync(session.Id);
            var messagesBefore = await store.GetMessagesAsync(session.Id);

            // Reload: a fresh store instance over the same database file is
            // the process-restart case — nothing may depend on the first
            // instance's memory.
            var reloaded = new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);

            var listedAfter = await reloaded.ListCheckpointsAsync(session.Id);
            await Assert.That(listedAfter).IsSuccessful();
            await Assert.That(listedAfter.Value.Count).IsEqualTo(listedBefore.Value.Count);
            for (int i = 0; i < listedBefore.Value.Count; i++)
            {
                await Assert.That(listedAfter.Value[i].Id).IsEqualTo(listedBefore.Value[i].Id);
                await Assert.That(listedAfter.Value[i].MessageId).IsEqualTo(listedBefore.Value[i].MessageId);
                await Assert.That(listedAfter.Value[i].MessageIndex).IsEqualTo(listedBefore.Value[i].MessageIndex);
                await Assert.That(listedAfter.Value[i].Label).IsEqualTo(listedBefore.Value[i].Label);
                await Assert.That(listedAfter.Value[i].RewindOf).IsEqualTo(listedBefore.Value[i].RewindOf);
                await Assert.That(listedAfter.Value[i].Removed).IsEqualTo(listedBefore.Value[i].Removed);
                await Assert.That(listedAfter.Value[i].CreatedAt).IsEqualTo(listedBefore.Value[i].CreatedAt);
            }

            var messagesAfter = await reloaded.GetMessagesAsync(session.Id);
            await Assert.That(messagesAfter.Value.Count).IsEqualTo(messagesBefore.Value.Count);
            for (int i = 0; i < messagesBefore.Value.Count; i++)
            {
                await Assert.That(messagesAfter.Value[i].Id).IsEqualTo(messagesBefore.Value[i].Id);
                await Assert.That(((UserMessage)messagesAfter.Value[i]).Content)
                    .IsEqualTo(((UserMessage)messagesBefore.Value[i]).Content);
            }

            // The reloaded store can keep working: a second rewind to the
            // restored checkpoint reports the single message appended since.
            await reloaded.AppendMessageAsync(session.Id, Msg(session.Id, 99));
            var again = await reloaded.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(again).IsSuccessful();
            await Assert.That(again.Value.Removed).IsEqualTo(1);
            await Assert.That(again.Value.Remaining).IsEqualTo(2);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task Unknown_Checkpoint_Id_Fails_Without_Touching_History()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 2);

            var result = await store.RewindToCheckpointAsync(session.Id, "no-such-checkpoint");

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("no-such-checkpoint");
            await Assert.That(result.Error).Contains(session.Id);

            var read = await store.GetMessagesAsync(session.Id);
            await Assert.That(read.Value.Count).IsEqualTo(2);
            await Assert.That(read.Value[0].Id).IsEqualTo(ids[0]);
            await Assert.That(read.Value[1].Id).IsEqualTo(ids[1]);

            // A failed rewind records no trail marker either.
            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(0);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task Trail_Marker_Is_Not_A_Rewind_Target()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 3);
            var checkpoint = (await store.CreateCheckpointAsync(session.Id, ids[0])).Value;
            await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);

            var listed = await store.ListCheckpointsAsync(session.Id);
            string trailId = listed.Value[1].Id;

            var result = await store.RewindToCheckpointAsync(session.Id, trailId);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains(trailId);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task Checkpoint_Boundaries_First_And_Last_Anchor()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 3);

            // Last anchor: a rewind of zero — the whole history is kept.
            var last = (await store.CreateCheckpointAsync(session.Id, ids[2])).Value;
            await Assert.That(last.MessageIndex).IsEqualTo(2);
            var zero = await store.RewindToCheckpointAsync(session.Id, last.Id);
            await Assert.That(zero).IsSuccessful();
            await Assert.That(zero.Value.Removed).IsEqualTo(0);
            await Assert.That(zero.Value.Remaining).IsEqualTo(3);

            // First anchor: everything after the head is dropped.
            var first = (await store.CreateCheckpointAsync(session.Id, ids[0], "head")).Value;
            await Assert.That(first.MessageIndex).IsEqualTo(0);
            var one = await store.RewindToCheckpointAsync(session.Id, first.Id);
            await Assert.That(one).IsSuccessful();
            await Assert.That(one.Value.Removed).IsEqualTo(2);
            await Assert.That(one.Value.Remaining).IsEqualTo(1);

            var reread = await store.GetMessagesAsync(session.Id);
            await Assert.That(reread.Value.Count).IsEqualTo(1);
            await Assert.That(reread.Value[0].Id).IsEqualTo(ids[0]);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task Missing_Session_And_Message_Fail_With_Shared_Shapes()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            await SeedAsync(store, session.Id, 1);

            var missingSession = await store.ListCheckpointsAsync("no-such-session");
            await Assert.That(missingSession).IsFailed();
            await Assert.That(missingSession.Error).IsEqualTo("Session 'no-such-session' not found.");

            var rewindMissingSession = await store.RewindToCheckpointAsync("no-such-session", "no-such-checkpoint");
            await Assert.That(rewindMissingSession).IsFailed();
            await Assert.That(rewindMissingSession.Error).IsEqualTo("Session 'no-such-session' not found.");

            var checkpointMissingSession = await store.CreateCheckpointAsync("no-such-session", "m");
            await Assert.That(checkpointMissingSession).IsFailed();
            await Assert.That(checkpointMissingSession.Error).IsEqualTo("Session 'no-such-session' not found.");

            var missingMessage = await store.CreateCheckpointAsync(session.Id, "no-such-message");
            await Assert.That(missingMessage).IsFailed();
            await Assert.That(missingMessage.Error)
                .IsEqualTo($"Message 'no-such-message' not found in session '{session.Id}'.");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task DeleteSession_Drops_Its_Checkpoints()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 2);
            await store.CreateCheckpointAsync(session.Id, ids[0]);

            var deleted = await store.DeleteAsync(session.Id);
            await Assert.That(deleted).IsSuccessful();

            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed).IsFailed();
            await Assert.That(listed.Error).IsEqualTo($"Session '{session.Id}' not found.");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }
}
