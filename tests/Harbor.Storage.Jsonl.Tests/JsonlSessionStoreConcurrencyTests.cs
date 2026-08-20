using System.Reflection;
using System.Collections.Concurrent;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

public class JsonlSessionStoreConcurrencyTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    [Test]
    public async Task ConcurrentWrites_DifferentSessions_DoNotBlock()
    {
        var store = CreateStore();
        try
        {
            const int sessionCount = 5;
            const int messagesPerSession = 10;

            var tasks = new Task[sessionCount];
            var sessionIds = new string[sessionCount];

            for (int i = 0; i < sessionCount; i++)
            {
                int index = i;
                tasks[i] = Task.Run(async () =>
                {
                    var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
                    sessionIds[index] = session.Id;

                    for (int j = 0; j < messagesPerSession; j++)
                    {
                        var msg = new UserMessage(
                            $"msg-{index}-{j}",
                            session.Id,
                            DateTimeOffset.UtcNow,
                            $"message {j}",
                            "code",
                            "claude-opus-4");

                        var result = await store.AppendMessageAsync(session.Id, msg);
                    }
                });
            }

            await Task.WhenAll(tasks);

            foreach (var sid in sessionIds)
            {
                var messages = await store.GetMessagesAsync(sid);
                await Assert.That(messages.IsSuccess).IsTrue();
                await Assert.That(messages.Value.Count).IsEqualTo(messagesPerSession);
            }
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task ConcurrentWrites_SameSession_AreSerialized()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            const int taskCount = 3;
            const int messagesPerTask = 10;

            var tasks = new Task[taskCount];

            for (int i = 0; i < taskCount; i++)
            {
                int index = i;
                tasks[i] = Task.Run(async () =>
                {
                    for (int j = 0; j < messagesPerTask; j++)
                    {
                        var msg = new UserMessage(
                            $"msg-{index}-{j}",
                            session.Id,
                            DateTimeOffset.UtcNow,
                            $"message {j} from task {index}",
                            "code",
                            "claude-opus-4");

                        var result = await store.AppendMessageAsync(session.Id, msg);
                    }
                });
            }

            await Task.WhenAll(tasks);

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(taskCount * messagesPerTask);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task SemaphoreReleased_OnDelete()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;

            var msg = new UserMessage(
                "msg-1",
                session.Id,
                DateTimeOffset.UtcNow,
                "hello",
                "code",
                "claude-opus-4");

            await store.AppendMessageAsync(session.Id, msg);

            var deleteResult = await store.DeleteAsync(session.Id);
            await Assert.That(deleteResult.IsSuccess).IsTrue();

            var locks = GetSessionLocks(store);
            await Assert.That(locks.ContainsKey(session.Id)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    private static ConcurrentDictionary<string, SemaphoreSlim> GetSessionLocks(JsonlSessionStore store)
    {
        var field = typeof(JsonlSessionStore).GetField("_sessionLocks", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ConcurrentDictionary<string, SemaphoreSlim>)field!.GetValue(store)!;
    }
}
