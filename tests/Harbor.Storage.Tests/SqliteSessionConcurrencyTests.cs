using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

/// <summary>
///     Issue #184 acceptance: the SQLite store serializes same-session
///     mutations while letting different sessions proceed concurrently
///     (per-session <c>SemaphoreSlim</c> strip, not the former global lock).
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteSessionConcurrencyTests
{
    private static string NewTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-sqlite-conc-{Guid.NewGuid():N}.db");

    private static SqliteSessionStore Create(out string dbPath)
    {
        dbPath = NewTempDbPath();
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static void Cleanup(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    [Test]
    public async Task ConcurrentWrites_DifferentSessions_AllPersist()
    {
        var store = Create(out string dbPath);
        try
        {
            const int sessionCount = 4;
            const int messagesPerSession = 10;

            var sessionIds = new string[sessionCount];
            for (int i = 0; i < sessionCount; i++)
                sessionIds[i] = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value.Id;

            var tasks = new Task[sessionCount];
            for (int i = 0; i < sessionCount; i++)
            {
                int index = i;
                tasks[i] = Task.Run(async () =>
                {
                    for (int j = 0; j < messagesPerSession; j++)
                    {
                        var msg = new UserMessage(
                            $"msg-{index}-{j}",
                            sessionIds[index],
                            DateTimeOffset.UtcNow,
                            $"message {j}",
                            "code",
                            "claude-opus-4");
                        var appended = await store.AppendMessageAsync(sessionIds[index], msg);
                        await Assert.That(appended.IsSuccess).IsTrue();
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
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task ConcurrentWrites_SameSession_AreSerialized()
    {
        var store = Create(out string dbPath);
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
                        var appended = await store.AppendMessageAsync(session.Id, msg);
                        await Assert.That(appended.IsSuccess).IsTrue();
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
            Cleanup(dbPath);
        }
    }
}
