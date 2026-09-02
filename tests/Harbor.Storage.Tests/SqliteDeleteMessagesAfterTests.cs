using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

public class SqliteDeleteMessagesAfterTests
{
    private static SqliteSessionStore Create() =>
        new(Path.Combine(Path.GetTempPath(), $"harbor-sqlite-{Guid.NewGuid():N}.db"), NullLogger<SqliteSessionStore>.Instance);

    [Test]
    public async Task Truncate_Keeps_Anchor_Deletes_Tail_Reports_Count()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
        List<string> ids = [];
        for (int i = 0; i < 4; i++)
        {
            var message = new UserMessage(
                $"msg-{i:D2}-{Guid.NewGuid():N}",
                session.Id,
                DateTimeOffset.UtcNow.AddMilliseconds(i * 5), // distinct created_at for stable ordering
                $"message {i}",
                "code",
                "test-model");
            await store.AppendMessageAsync(session.Id, message);
            ids.Add(message.Id);
        }

        var result = await store.DeleteMessagesAfterAsync(session.Id, ids[1]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(2);

        var read = await store.GetMessagesAsync(session.Id);
        await Assert.That(read.Value.Count).IsEqualTo(2);
        await Assert.That(read.Value[0].Id).IsEqualTo(ids[0]);
        await Assert.That(read.Value[1].Id).IsEqualTo(ids[1]);
    }

    [Test]
    public async Task Unknown_Message_Fails()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;

        var result = await store.DeleteMessagesAfterAsync(session.Id, "no-such-message");

        await Assert.That(result.IsFailure).IsTrue();
    }
}
