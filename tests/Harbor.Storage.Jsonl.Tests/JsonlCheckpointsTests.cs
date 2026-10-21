using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Slice-1 tests for #1247 (PX1 checkpoints/rewind, jsonl only):
///     checkpoint markers record the message index and stay invisible to
///     history reads; rewind truncates to the anchor and leaves an honest
///     trail marker; later checkpoints go with the dropped future; unknown
///     checkpoint ids fail without touching the file.
/// </summary>
public class JsonlCheckpointsTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    private static UserMessage Msg(string sessionId, int i) => new(
        $"msg-{i:D2}-{Guid.NewGuid():N}",
        sessionId,
        DateTimeOffset.UtcNow.AddMilliseconds(i),
        $"message {i}",
        "code",
        "claude");

    private static readonly DateTimeOffset TestTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static async Task<string> SeedAsync(JsonlSessionStore store, string sessionId, int count)
    {
        for (int i = 0; i < count; i++)
            await store.AppendMessageAsync(sessionId, Msg(sessionId, i));
        return sessionId;
    }

    [Test]
    public async Task CreateCheckpoint_RecordsIndex_AndLeavesHistoryUntouched()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await SeedAsync(store, session.Id, 3);
            var anchor = (await store.GetMessagesAsync(session.Id)).Value[1];

            var marked = await store.CreateCheckpointAsync(session.Id, anchor.Id, "before-refactor");
            await Assert.That(marked.IsSuccess).IsTrue();
            await Assert.That(marked.Value.MessageId).IsEqualTo(anchor.Id);
            await Assert.That(marked.Value.MessageIndex).IsEqualTo(1);
            await Assert.That(marked.Value.Label).IsEqualTo("before-refactor");

            // Checkpoints are metadata: history reads see exactly the messages.
            var reread = await store.GetMessagesAsync(session.Id);
            await Assert.That(reread.Value.Count).IsEqualTo(3);

            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(1);
            await Assert.That(listed.Value[0].Id).IsEqualTo(marked.Value.Id);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task RewindToCheckpoint_Truncates_AndLeavesHonestTrail()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await SeedAsync(store, session.Id, 4);
            var anchor = (await store.GetMessagesAsync(session.Id)).Value[1];
            var checkpoint = (await store.CreateCheckpointAsync(session.Id, anchor.Id)).Value;

            var rewound = await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(rewound.IsSuccess).IsTrue();
            await Assert.That(rewound.Value.Removed).IsEqualTo(2);
            await Assert.That(rewound.Value.Remaining).IsEqualTo(2);

            var reread = await store.GetMessagesAsync(session.Id);
            await Assert.That(reread.Value.Count).IsEqualTo(2);
            await Assert.That(reread.Value[1].Id).IsEqualTo(anchor.Id);

            // The honest trail: the checkpoint survives, and a trail marker
            // records the rewind — without leaking into message history.
            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(2);
            await Assert.That(listed.Value[0].Id).IsEqualTo(checkpoint.Id);
            await Assert.That(listed.Value[1].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value[1].RewindOf).IsEqualTo(checkpoint.Id);
            await Assert.That(listed.Value[1].Removed).IsEqualTo(2);

            // Appending after a rewind works.
            await store.AppendMessageAsync(session.Id, Msg(session.Id, 99));
            var after = await store.GetMessagesAsync(session.Id);
            await Assert.That(after.Value.Count).IsEqualTo(3);

            // The restored checkpoint survived, so rewinding there twice works.
            var rewoundAgain = await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(rewoundAgain.IsSuccess).IsTrue();
            await Assert.That(rewoundAgain.Value.Removed).IsEqualTo(1);
            await Assert.That(rewoundAgain.Value.Remaining).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task RewindToCheckpoint_KeepsTheLaterCheckpoints_AndLeavesAnHonestTrail()
    {
        // #1752: the restore is an append-only rewind, so it no longer
        // REWRITES the file — and the checkpoint over a message that left the
        // surface stays in the log with it. The checkpoint is a record of
        // where the user marked a point; dropping it would destroy exactly
        // what the append-only log exists to keep. What says the restore
        // happened is the trail marker beside them.
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await SeedAsync(store, session.Id, 4);
            var messages = (await store.GetMessagesAsync(session.Id)).Value;
            var early = (await store.CreateCheckpointAsync(session.Id, messages[0].Id, "early")).Value;
            var late = await store.CreateCheckpointAsync(session.Id, messages[2].Id, "late");

            var rewound = await store.RewindToCheckpointAsync(session.Id, early.Id);
            await Assert.That(rewound.IsSuccess).IsTrue();
            await Assert.That(rewound.Value.Removed).IsEqualTo(3);
            await Assert.That(rewound.Value.Remaining).IsEqualTo(1);

            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(3);
            await Assert.That(listed.Value[0].Id).IsEqualTo(early.Id);
            await Assert.That(listed.Value[1].Id).IsEqualTo(late.Value.Id);
            await Assert.That(listed.Value[2].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value[2].RewindOf).IsEqualTo(early.Id);
            await Assert.That(listed.Value[2].Removed).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task Unknown_Checkpoint_Id_Fails_Without_Touching_The_File()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, Msg(session.Id, 0));

            long before = File.GetLastWriteTimeUtc(store.GetRootDirectory()).Ticks;
            var result = await store.RewindToCheckpointAsync(session.Id, "no-such-checkpoint");
            long after = File.GetLastWriteTimeUtc(store.GetRootDirectory()).Ticks;

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("no-such-checkpoint");
            await Assert.That(after).IsEqualTo(before);

            var read = await store.GetMessagesAsync(session.Id);
            await Assert.That(read.Value.Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task Checkpoint_After_Rewind_Anchors_At_The_New_Tail()
    {
        // #1551: the restore absorbs the memento, then capture runs the same
        // originator path again — the new checkpoint's index must come from
        // the post-rewind message order, not from any future it replaced.
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await SeedAsync(store, session.Id, 4);
            var anchor = (await store.GetMessagesAsync(session.Id)).Value[1];
            var checkpoint = (await store.CreateCheckpointAsync(session.Id, anchor.Id)).Value;

            var rewound = await store.RewindToCheckpointAsync(session.Id, checkpoint.Id);
            await Assert.That(rewound.IsSuccess).IsTrue();

            // The trail marker exists already; a new capture over the new tail
            // anchors at index 1 (the tail itself) in the truncated order.
            var afterRewind = (await store.GetMessagesAsync(session.Id)).Value;
            var recaptured = await store.CreateCheckpointAsync(session.Id, anchor.Id, "post-rewind");
            await Assert.That(recaptured.IsSuccess).IsTrue();
            await Assert.That(recaptured.Value.MessageIndex).IsEqualTo(afterRewind.Count - 1);
            await Assert.That(recaptured.Value.MessageId).IsEqualTo(anchor.Id);
            await Assert.That(recaptured.Value.Label).IsEqualTo("post-rewind");

            var rewoundAgain = await store.RewindToCheckpointAsync(session.Id, recaptured.Value.Id);
            await Assert.That(rewoundAgain.IsSuccess).IsTrue();
            await Assert.That(rewoundAgain.Value.Removed).IsEqualTo(0);
            await Assert.That(rewoundAgain.Value.Remaining).IsEqualTo(2);

            // Four rows now: capture, trail of the first rewind, second
            // capture, trail of the second (this one) — in file order, none of
            // them leaking into history.
            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.Value.Count).IsEqualTo(4);
            await Assert.That(listed.Value[0].Id).IsEqualTo(checkpoint.Id);
            await Assert.That(listed.Value[1].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value[2].Id).IsEqualTo(recaptured.Value.Id);
            await Assert.That(listed.Value[3].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value[3].RewindOf).IsEqualTo(recaptured.Value.Id);
            await Assert.That(listed.Value[3].Removed).IsEqualTo(0);

            var read = await store.GetMessagesAsync(session.Id);
            await Assert.That(read.Value.Count).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task MapperKit_Conversions_Stay_Inside_One_Border()
    {
        // #1551: the three shapes meet only in the kit — round-trips must not
        // invent or lose state at the borders.
        var row = new CheckpointEntry("checkpoint", "ck-000", "msg-a", 3, TestTime, "label");

        var contract = SessionCheckpointMapperKit.ToContract(row, "s1");
        await Assert.That(contract.Id).IsEqualTo("ck-000");
        await Assert.That(contract.SessionId).IsEqualTo("s1");
        await Assert.That(contract.MessageId).IsEqualTo("msg-a");
        await Assert.That(contract.MessageIndex).IsEqualTo(3);
        await Assert.That(contract.Label).IsEqualTo("label");
        await Assert.That(contract.IsRewindTrail).IsFalse();

        var memento = SessionCheckpointMapperKit.ToMemento(contract);
        await Assert.That(memento.Id).IsEqualTo("ck-000");
        await Assert.That(memento.MessageId).IsEqualTo("msg-a");
        await Assert.That(memento.MessageIndex).IsEqualTo(3);
        await Assert.That(memento.CreatedAt).IsEqualTo(TestTime);
        await Assert.That(memento.Label).IsEqualTo("label");

        var persisted = SessionCheckpointMapperKit.ToRow(memento);
        await Assert.That(persisted.Type).IsEqualTo("checkpoint");
        await Assert.That(persisted.Id).IsEqualTo(row.Id);
        await Assert.That(persisted.MessageId).IsEqualTo(row.MessageId);
        await Assert.That(persisted.MessageIndex).IsEqualTo(row.MessageIndex);
        await Assert.That(persisted.CreatedAt).IsEqualTo(row.CreatedAt);
        await Assert.That(persisted.Label).IsEqualTo(row.Label);
        await Assert.That(persisted.RewindOf).IsEqualTo(row.RewindOf);
        await Assert.That(persisted.Removed).IsEqualTo(row.Removed);

        var fromMemento = SessionCheckpointMapperKit.ToContract(memento, "s1");
        await Assert.That(fromMemento).IsEqualTo(contract);

        var trail = SessionCheckpointMapperKit.ToTrailRow(memento, removed: 5, now: TestTime);
        await Assert.That(trail.Type).IsEqualTo("checkpoint");
        await Assert.That(trail.Id).IsNotEqualTo("ck-000"); // fresh trail id
        await Assert.That(trail.MessageId).IsEqualTo("msg-a");
        await Assert.That(trail.MessageIndex).IsEqualTo(3);
        await Assert.That(trail.Label).IsNull();
        await Assert.That(trail.RewindOf).IsEqualTo("ck-000");
        await Assert.That(trail.Removed).IsEqualTo(5);

        var trailContract = SessionCheckpointMapperKit.ToContract(trail, "s1");
        await Assert.That(trailContract.IsRewindTrail).IsTrue();
        await Assert.That(trailContract.RewindOf).IsEqualTo("ck-000");
        await Assert.That(trailContract.Removed).IsEqualTo(5);
    }
}
