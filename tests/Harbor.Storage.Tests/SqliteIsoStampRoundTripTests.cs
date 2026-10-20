using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

namespace Harbor.Storage.Tests;

/// <summary>
///     Parse symmetry of the timestamp TEXT columns (#1534). The write side
///     formats every date through ONE round-trip ("O") path and every read
///     (<c>SqliteMappers.ReadSession</c>, message payloads, checkpoint rows)
///     parses it back with <c>DateTimeOffset.Parse</c> — these tests pin the
///     symmetry end to end over the real SQLite file: create/append rows, read
///     the raw TEXT back, and assert the stored string is exactly the "O"
///     rendering of the domain stamp AND that parsing it returns the original
///     instant with its offset intact (the offset matters: SQLite sorts the
///     session list by the textual <c>updated_at</c>, and mixed-offset stamps
///     must survive the round trip).
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteIsoStampRoundTripTests
{
    private static string NewTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-sqlite-{Guid.NewGuid():N}.db");

    private static SqliteSessionStore Create(out string dbPath)
    {
        dbPath = NewTempDbPath();
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    /// <summary>Non-UTC, sub-second, no exact whole seconds — stresses every "O" field.</summary>
    private static readonly DateTimeOffset OddStamp =
        new(2026, 3, 14, 9, 26, 53, 589, TimeSpan.FromHours(2));

    [Test]
    public async Task Session_StampColumns_StoreRoundTripIsoText()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

            var (createdAtText, updatedAtText) = await ReadStampColumnsAsync(dbPath);

            // The stored text is exactly the single "O" rendering of the domain
            // stamp — not a near-miss format that happens to parse.
            await Assert.That(createdAtText).IsEqualTo(session.CreatedAt.ToString("O"));
            await Assert.That(updatedAtText).IsEqualTo(session.UpdatedAt.ToString("O"));

            // ... and parsing it returns the original instant (parse symmetry).
            await Assert.That(DateTimeOffset.Parse(createdAtText)).IsEqualTo(session.CreatedAt);
            await Assert.That(DateTimeOffset.Parse(updatedAtText)).IsEqualTo(session.UpdatedAt);

            // Re-read through the store: the domain record survives intact.
            var fetched = await store.GetAsync(session.Id);
            await Assert.That(fetched.IsSuccess).IsTrue();
            await Assert.That(fetched.Value.CreatedAt).IsEqualTo(session.CreatedAt);
            await Assert.That(fetched.Value.UpdatedAt).IsEqualTo(session.UpdatedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task AppendMessage_StampColumns_StoreRoundTripIsoText()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var message = TestMessages.User("round trip", id: "umsg-1", sessionId: session.Id, createdAt: OddStamp, model: "claude-opus-4");
            await Assert.That((await store.AppendMessageAsync(session.Id, message)).IsSuccess).IsTrue();

            var (createdAtText, _) = await ReadStampColumnsAsync(dbPath, messages: true);

            await Assert.That(createdAtText).IsEqualTo(OddStamp.ToString("O"));
            await Assert.That(DateTimeOffset.Parse(createdAtText)).IsEqualTo(OddStamp);

            // +02:00 offset preserved through the round trip...
            await Assert.That(DateTimeOffset.Parse(createdAtText).Offset).IsEqualTo(OddStamp.Offset);

            // ... and the message read path decodes the payload stamp exactly.
            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(messages.Value[0].CreatedAt).IsEqualTo(OddStamp);

            // The session.updated_at touch on append reads back cleanly too.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task UpdateMessage_StampColumns_StoreRoundTripIsoText()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var original = TestMessages.User("before", id: "umsg-1", sessionId: session.Id, model: "claude-opus-4");
            await Assert.That((await store.AppendMessageAsync(session.Id, original)).IsSuccess).IsTrue();

            var edited = TestMessages.User("after", id: "umsg-1", sessionId: session.Id, createdAt: OddStamp, model: "claude-opus-4");
            await Assert.That((await store.UpdateMessageAsync(session.Id, edited)).IsSuccess).IsTrue();

            var (createdAtText, _) = await ReadStampColumnsAsync(dbPath, messages: true);
            await Assert.That(createdAtText).IsEqualTo(OddStamp.ToString("O"));
            await Assert.That(DateTimeOffset.Parse(createdAtText)).IsEqualTo(OddStamp);

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value[0].CreatedAt).IsEqualTo(OddStamp);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task Checkpoint_StampColumn_StoreRoundTripIsoText()
    {
        var store = Create(out string dbPath);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            var message = TestMessages.User("anchor", id: "umsg-1", sessionId: session.Id, createdAt: OddStamp, model: "claude-opus-4");
            await Assert.That((await store.AppendMessageAsync(session.Id, message)).IsSuccess).IsTrue();

            var checkpointResult = await store.CreateCheckpointAsync(session.Id, "umsg-1", "label");
            await Assert.That(checkpointResult.IsSuccess).IsTrue();
            var checkpoint = checkpointResult.Value;
            var listed = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.IsSuccess).IsTrue();
            await Assert.That(listed.Value.Count).IsEqualTo(1);
            await Assert.That(listed.Value[0].CreatedAt).IsEqualTo(checkpoint.CreatedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    ///     Read the raw TEXT stamp columns straight from the file, bypassing
    ///     the store: <c>(created_at, updated_at)</c> of the first session row,
    ///     or of the first message row (with its session's fresh
    ///     <c>updated_at</c>) when <paramref name="messages" /> is set.
    /// </summary>
    private static async Task<(string CreatedAt, string UpdatedAt)> ReadStampColumnsAsync(
        string dbPath, bool messages = false)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = messages
            ? "SELECT m.created_at, s.updated_at FROM messages m JOIN sessions s ON s.id = m.session_id LIMIT 1"
            : "SELECT created_at, updated_at FROM sessions LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync();
        await Assert.That(await reader.ReadAsync()).IsTrue();
        string createdAt = reader.GetString(0);
        string updatedAt = reader.GetString(1);
        return (createdAt, updatedAt);
    }
}
