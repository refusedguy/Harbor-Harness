using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

/// <summary>
///     Issue #199 acceptance: the SQLite store honors the same
///     <c>Result</c> failure shapes as the JSONL store — missing sessions and
///     messages are failures (never throws), and one unreadable row never
///     fails the whole history.
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteSessionStoreRopTests
{
    private static string NewTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-sqlite-rop-{Guid.NewGuid():N}.db");

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
    public async Task GetMessagesAsync_MissingSession_ReturnsSharedNotFoundShape()
    {
        var store = Create(out string dbPath);
        try
        {
            var result = await store.GetMessagesAsync("nonexistent-id");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).IsEqualTo("Session 'nonexistent-id' not found.");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task GetMessagesAsync_CorruptRow_SkipsRowAndKeepsRest()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m-good", session.Id, DateTimeOffset.UtcNow, "kept", "code", "claude-opus-4"));

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO messages (id, session_id, parent_id, role, agent, model, created_at, created_at_ms, payload)
                    VALUES ('m-bad', @sid, NULL, 'user', 'code', 'claude-opus-4', @created, 0, '{broken json')
                    """;
                cmd.Parameters.AddWithValue("@sid", session.Id);
                cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            var messages = await store.GetMessagesAsync(session.Id);

            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("kept");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task GetStatsAsync_CorruptMetadata_ReturnsFailureNamingSession()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE sessions SET metadata = '###not-json###' WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", session.Id);
                cmd.ExecuteNonQuery();
            }

            var stats = await store.GetStatsAsync(session.Id);

            await Assert.That(stats.IsFailure).IsTrue();
            await Assert.That(stats.Error).Contains(session.Id);
            await Assert.That(stats.Error).Contains("corrupt");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Test]
    public async Task DeleteMessagesAfterAsync_UnknownMessage_FailureNamesMessageAndSession()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m1", session.Id, DateTimeOffset.UtcNow, "hello", "code", "claude-opus-4"));

            var result = await store.DeleteMessagesAfterAsync(session.Id, "no-such-message");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).IsEqualTo(
                $"Message 'no-such-message' not found in session '{session.Id}'.");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }
}
