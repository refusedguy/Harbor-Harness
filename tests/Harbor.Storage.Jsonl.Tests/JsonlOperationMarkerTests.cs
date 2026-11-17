// JsonlOperationMarkerTests.cs — the #2226 batch primitive and fork/turn
// crash markers in the JSONL session file.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Storage.Jsonl.Tests;

public class JsonlOperationMarkerTests
{
    private static JsonlSessionStore CreateStore(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"harbor-ops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    [Test]
    public async Task AppendMessagesBatchAsync_WritesWholeBatch_AsOneAtomicRewrite()
    {
        var store = CreateStore(out string tempDir);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test/m")).Value;
            var batch = new AgentMessage[4];
            for (int i = 0; i < batch.Length; i++)
            {
                batch[i] = TestMessages.User($"batch-{i}", id: $"m{i}", sessionId: session.Id, model: "test/m");
            }

            Result written = await store.AppendMessagesBatchAsync(session.Id, batch);

            await Assert.That(written.IsSuccess).IsTrue();
            IReadOnlyList<AgentMessage> read = (await store.GetMessagesAsync(session.Id)).Value;
            await Assert.That(read.Count).IsEqualTo(4);
            for (int i = 0; i < batch.Length; i++)
            {
                await Assert.That(read[i].Id).IsEqualTo($"m{i}");
            }

            // The atomic rewrite cleans its temp sibling up.
            string path = Path.Combine(tempDir, $"{session.Id}.jsonl");
            await Assert.That(Directory.GetFiles(tempDir, "*.tmp")).IsEmpty();
            string raw = await File.ReadAllTextAsync(path);
            await Assert.That(raw.Split("\"type\":\"message\"").Length - 1).IsEqualTo(4);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task OperationMarkers_RoundTrip_AndNeverEnterTheMessageHistory()
    {
        var store = CreateStore(out string tempDir);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "test", "test/m")).Value;
            await store.AppendMessagesBatchAsync(session.Id,
                [TestMessages.User("hi", id: "m1", sessionId: session.Id, model: "test/m")]);

            await store.AppendOperationEventAsync(session.Id, new SessionOperationEvent
            {
                Kind = SessionOperationKind.ForkStart,
                Owner = "fork-1",
                BoundaryMessageId = "m1",
                Copied = 1,
                OccurredAt = DateTimeOffset.UtcNow,
            });

            IReadOnlyList<AgentMessage> messages = (await store.GetMessagesAsync(session.Id)).Value;
            await Assert.That(messages.Count).IsEqualTo(1);
            await Assert.That(messages[0].Id).IsEqualTo("m1");

            IReadOnlyList<SessionOperationEvent> markers = (await store.GetOperationEventsAsync(session.Id)).Value;
            await Assert.That(markers.Count).IsEqualTo(1);
            await Assert.That(markers[0].Kind).IsEqualTo(SessionOperationKind.ForkStart);
            await Assert.That(markers[0].BoundaryMessageId).IsEqualTo("m1");
            await Assert.That(markers[0].Copied).IsEqualTo(1);
            await Assert.That(SessionOperationMarkers.HasIncompleteFork(markers)).IsTrue();

            await store.AppendOperationEventAsync(session.Id, new SessionOperationEvent
            {
                Kind = SessionOperationKind.ForkEnd,
                Owner = "fork-1",
                Copied = 1,
                OccurredAt = DateTimeOffset.UtcNow,
            });

            IReadOnlyList<SessionOperationEvent> closed = (await store.GetOperationEventsAsync(session.Id)).Value;
            await Assert.That(SessionOperationMarkers.HasIncompleteFork(closed)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
