using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Storage.Tests;
/// <summary>
///     Tests for SqliteSessionStore — uses a temp file per test and deletes it in finally.
///     Verifies CRUD, message ordering, and cascading delete of messages.
/// </summary>
public class SqliteSessionStoreTests
{
    private static string NewTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-sqlite-{Guid.NewGuid():N}.db");

    private static SqliteSessionStore Create(out string dbPath)
    {
        dbPath = NewTempDbPath();
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static UserMessage NewUserMessage(string sessionId, string content, string idSuffix = "")
        => new(
            $"umsg-{idSuffix}{Guid.NewGuid():N}",
            sessionId,
            DateTimeOffset.UtcNow,
            content,
            "code",
            "claude-opus-4");

    [Test]
    public async Task CreateAsync_ReturnsSessionWithValidId()
    {
        var store = Create(out string dbPath);
        try
        {
            var result = await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(string.IsNullOrEmpty(result.Value.Id)).IsFalse();
            await Assert.That(result.Value.Directory).IsEqualTo("/proj");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
            await Assert.That(result.Value.ProviderId).IsEqualTo("anthropic");
            await Assert.That(result.Value.Model).IsEqualTo("claude-opus-4");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task GetAsync_ReturnsCreatedSession()
    {
        var store = Create(out string dbPath);
        try
        {
            var created = await store.CreateAsync("/proj", "code", "openai", "gpt-4o");

            var fetched = await store.GetAsync(created.Value.Id);

            await Assert.That(fetched.IsSuccess).IsTrue();
            await Assert.That(fetched.Value.Id).IsEqualTo(created.Value.Id);
            await Assert.That(fetched.Value.ProviderId).IsEqualTo("openai");
            await Assert.That(fetched.Value.Model).IsEqualTo("gpt-4o");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task GetAsync_UnknownId_ReturnsFailure()
    {
        var store = Create(out string dbPath);
        try
        {
            var result = await store.GetAsync("nonexistent-id");
            await Assert.That(result.IsFailure).IsTrue();
            // П.24: absence reads as a clean "not found" outcome, not a raw provider error.
            await Assert.That(result.Error).IsEqualTo("Session 'nonexistent-id' not found.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task GetAsync_CancelledToken_PropagatesCancellationInsteadOfFailure()
    {
        var store = Create(out string dbPath);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // П.24: cancellation is NOT a storage failure — it propagates (Esc semantics).
            await Assert.That(async () => await store.GetAsync("any", cts.Token))
                .Throws<OperationCanceledException>();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task ListAsync_ReturnsAllCreatedSessions()
    {
        var store = Create(out string dbPath);
        try
        {
            await store.CreateAsync("/proj1", "code", "anthropic", "claude-opus-4");
            await store.CreateAsync("/proj2", "plan", "openai", "gpt-4o");
            await store.CreateAsync("/proj3", "explore", "ollama", "llama3.2");

            var list = await store.ListAsync();

            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Count).IsEqualTo(3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task ListAsync_FiltersByProjectId()
    {
        var store = Create(out string dbPath);
        try
        {
            var s1 = await store.CreateAsync("/projA", "code", "anthropic", "claude-opus-4");
            await store.CreateAsync("/projB", "code", "openai", "gpt-4o");

            var list = await store.ListAsync(s1.Value.ProjectId);

            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Count).IsEqualTo(1);
            await Assert.That(list.Value[0].ProjectId).IsEqualTo(s1.Value.ProjectId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task AppendMessageAsync_PersistsMessage()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var msg = NewUserMessage(session.Id, "hello", "1");

            var appendResult = await store.AppendMessageAsync(session.Id, msg);
            await Assert.That(appendResult.IsSuccess).IsTrue();

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("hello");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task GetMessagesAsync_ReturnsInCreatedAtOrder()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var baseTime = DateTimeOffset.UtcNow;

            // Insert messages with explicit, non-monotonic timestamps.
            // SqliteSessionStore orders by created_at ASC, so order should follow timestamps, not insertion order.
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m1", session.Id, baseTime.AddSeconds(10), "second-inserted-first-ts", "code", "claude"));
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m2", session.Id, baseTime.AddSeconds(5), "first", "code", "claude"));
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m3", session.Id, baseTime.AddSeconds(20), "third", "code", "claude"));

            var messages = await store.GetMessagesAsync(session.Id);

            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(3);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("first");
            await Assert.That(((UserMessage)messages.Value[1]).Content).IsEqualTo("second-inserted-first-ts");
            await Assert.That(((UserMessage)messages.Value[2]).Content).IsEqualTo("third");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task DeleteAsync_RemovesSessionAndMessages()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, NewUserMessage(session.Id, "msg1", "a"));
            await store.AppendMessageAsync(session.Id, NewUserMessage(session.Id, "msg2", "b"));
            await store.AppendMessageAsync(session.Id, NewUserMessage(session.Id, "msg3", "c"));

            var deleteResult = await store.DeleteAsync(session.Id);
            await Assert.That(deleteResult.IsSuccess).IsTrue();

            // Session itself is gone.
            var fetched = await store.GetAsync(session.Id);
            await Assert.That(fetched.IsFailure).IsTrue();

            // Messages are also gone (FK cascade).
            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task UpdateMessageAsync_ReplacesPayload()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var original = NewUserMessage(session.Id, "original", "1");
            await store.AppendMessageAsync(session.Id, original);

            var updated = original with { Content = "edited" };
            await store.UpdateMessageAsync(session.Id, updated);

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("edited");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task GetStatsAsync_ReturnsSessionMetadata()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var stats = await store.GetStatsAsync(session.Id);

            await Assert.That(stats.IsSuccess).IsTrue();
            await Assert.That(stats.Value.MessageCount).IsEqualTo(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task UpdateStatsAsync_PersistsMetadata()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

            var newMeta = new SessionMetadata(0.99m, 200, 100, 50, 5, 2, 4, null);
            var updateResult = await store.UpdateStatsAsync(session.Id, newMeta);
            await Assert.That(updateResult.IsSuccess).IsTrue();

            var stats = await store.GetStatsAsync(session.Id);
            await Assert.That(stats.Value.Cost).IsEqualTo(0.99m);
            await Assert.That(stats.Value.TokensInput).IsEqualTo(200);
            await Assert.That(stats.Value.TokensOutput).IsEqualTo(100);
            await Assert.That(stats.Value.TokensReasoning).IsEqualTo(50);
            await Assert.That(stats.Value.MessageCount).IsEqualTo(4);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task AppendMessageAsync_UpdatesSessionTimestamp()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var originalUpdatedAt = session.UpdatedAt;

            // Small delay to ensure UpdatedAt differs.
            await Task.Delay(50);
            await store.AppendMessageAsync(session.Id, NewUserMessage(session.Id, "hello"));

            var fetched = await store.GetAsync(session.Id);
            await Assert.That(fetched.IsSuccess).IsTrue();
            await Assert.That(fetched.Value.UpdatedAt).IsGreaterThan(originalUpdatedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
