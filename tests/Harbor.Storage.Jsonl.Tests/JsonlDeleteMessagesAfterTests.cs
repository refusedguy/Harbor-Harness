using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Round-trip tests for <c>DeleteMessagesAfterAsync</c> on the JSONL
///     backend: the truncate survives a store RE-CREATE (i.e. it is on-disk,
///     not just in the parse cache), the header line stays intact, and
///     unknown ids fail without touching the file.
/// </summary>
public class JsonlDeleteMessagesAfterTests
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

    [Test]
    public async Task Truncate_Persists_Across_StoreRecreate_AndKeepsHeader()
    {
        string root;
        (string SessionId, List<string> Ids) seeded;
        var first = CreateStore();
        try
        {
            var session = (await first.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            List<string> ids = [];
            for (int i = 0; i < 4; i++)
            {
                var message = Msg(session.Id, i);
                await first.AppendMessageAsync(session.Id, message);
                ids.Add(message.Id);
            }

            seeded = (session.Id, ids);
        }
        finally
        {
            root = first.GetRootDirectory();
        }

        var second = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var result = await second.DeleteMessagesAfterAsync(seeded.SessionId, seeded.Ids[1]);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo(2);

            // Re-create AGAIN: truncation must be durable on disk.
            var third = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
            try
            {
                var read = await third.GetMessagesAsync(seeded.SessionId);
                await Assert.That(read.Value.Count).IsEqualTo(2);

                var listed = await third.GetAsync(seeded.SessionId);
                await Assert.That(listed.IsSuccess).IsTrue(); // header survived

                // And appending after a rewind works.
                var fresh = Msg(seeded.SessionId, 99);
                var append = await third.AppendMessageAsync(seeded.SessionId, fresh);
                await Assert.That(append.IsSuccess).IsTrue();

                var reread = await third.GetMessagesAsync(seeded.SessionId);
                await Assert.That(reread.Value.Count).IsEqualTo(3);
            }
            finally
            {
                Directory.Delete(third.GetRootDirectory(), true);
            }
        }
        finally
        {
            if (Directory.Exists(second.GetRootDirectory()))
                Directory.Delete(second.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task Unknown_Message_Id_Fails_Without_Touching_The_File()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            var message = Msg(session.Id, 0);
            await store.AppendMessageAsync(session.Id, message);

            long before = File.GetLastWriteTimeUtc(store.GetRootDirectory()).Ticks;
            var result = await store.DeleteMessagesAfterAsync(session.Id, "no-such-message");
            long after = File.GetLastWriteTimeUtc(store.GetRootDirectory()).Ticks;

            await Assert.That(result.IsFailure).IsTrue();
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
