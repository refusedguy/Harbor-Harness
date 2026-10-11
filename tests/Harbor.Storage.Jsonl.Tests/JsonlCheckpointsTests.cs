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
    public async Task RewindToCheckpoint_DropsLaterCheckpoints_KeepsEarlierOnes()
    {
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
            await Assert.That(listed.Value.Count).IsEqualTo(2);
            await Assert.That(listed.Value[0].Id).IsEqualTo(early.Id);
            await Assert.That(listed.Value[1].IsRewindTrail).IsTrue();
            await Assert.That(listed.Value.Any(c => c.Id == late.Value.Id)).IsFalse();
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
}
