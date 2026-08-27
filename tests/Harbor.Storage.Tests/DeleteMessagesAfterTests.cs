using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;

namespace Harbor.Storage.Tests;

/// <summary>
///     "Rewind to here" semantics of <c>DeleteMessagesAfterAsync</c>: the
///     anchor message stays, everything appended after it is gone, and the
///     result reports how many messages were removed.
/// </summary>
public class DeleteMessagesAfterTests
{
    private static MemorySessionStore Create() => new();

    private async Task<(string SessionId, List<string> MessageIds)> SeedAsync(
        MemorySessionStore store, int count)
    {
        var created = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var message = new UserMessage(
                $"msg-{i:D2}-{Guid.NewGuid():N}",
                created.Id,
                DateTimeOffset.UtcNow.AddMilliseconds(i),
                $"message {i}",
                "code",
                "test-model");
            await store.AppendMessageAsync(created.Id, message);
            ids.Add(message.Id);
        }

        return (created.Id, ids);
    }

    private static async Task<List<AgentMessage>> ReadIdsAsync(MemorySessionStore store, string sessionId)
    {
        var read = await store.GetMessagesAsync(sessionId);
        return [.. read.Value];
    }

    [Test]
    public async Task Deletes_Tail_Keeps_Anchor_AndPrefix()
    {
        var store = Create();
        var (sessionId, ids) = await SeedAsync(store, 5);

        var result = await store.DeleteMessagesAfterAsync(sessionId, ids[1]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(3);

        var remaining = await ReadIdsAsync(store, sessionId);
        await Assert.That(remaining.Count).IsEqualTo(2);
        await Assert.That(remaining[0].Id).IsEqualTo(ids[0]);
        await Assert.That(remaining[1].Id).IsEqualTo(ids[1]);
    }

    [Test]
    public async Task Anchor_Is_Last_Message_Deletes_Nothing()
    {
        var store = Create();
        var (sessionId, ids) = await SeedAsync(store, 3);

        var result = await store.DeleteMessagesAfterAsync(sessionId, ids[^1]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(0);
        var remaining = await ReadIdsAsync(store, sessionId);
        await Assert.That(remaining.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Unknown_Message_Id_Fails()
    {
        var store = Create();
        var (sessionId, _) = await SeedAsync(store, 2);

        var result = await store.DeleteMessagesAfterAsync(sessionId, "no-such-message");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not found");
    }

    [Test]
    public async Task Unknown_Session_Id_Fails()
    {
        var store = Create();

        var result = await store.DeleteMessagesAfterAsync("no-such-session", "whatever");

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Truncate_Then_Append_Works_Afterwards()
    {
        var store = Create();
        var (sessionId, ids) = await SeedAsync(store, 4);
        await store.DeleteMessagesAfterAsync(sessionId, ids[0]);

        var fresh = new UserMessage(
            Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow, "fresh", "code", "test-model");
        var append = await store.AppendMessageAsync(sessionId, fresh);

        await Assert.That(append.IsSuccess).IsTrue();
        var remaining = await ReadIdsAsync(store, sessionId);
        await Assert.That(remaining.Count).IsEqualTo(2); // anchor + fresh
    }
}
