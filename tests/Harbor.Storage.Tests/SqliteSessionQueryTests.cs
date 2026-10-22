using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Sqlite;
using Harbor.TestKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

/// <summary>
///     The SQLite session query (#1743): filters and the window reach SQL, the
///     text filter is served by the FTS5 index when the SQLite build has one
///     and by the literal scan when it does not, and the hits come back ranked
///     and bounded.
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteSessionQueryTests
{
    private static readonly DateTimeOffset BaseInstant =
        new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static string NewTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-sqlite-query-{Guid.NewGuid():N}.db");

    private static SqliteSessionStore CreateStore(out string dbPath)
    {
        dbPath = NewTempDbPath();
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static SqliteSessionQuery CreateQuery(string dbPath, bool allowFullText = true) =>
        new(dbPath, NullLogger<SqliteSessionQuery>.Instance, allowFullText);

    private static UserMessage User(string sessionId, string content, DateTimeOffset at, string id) =>
        new(id, sessionId, at, content, "code", "claude-opus-4");

    private static AssistantMessage Assistant(
        string sessionId,
        string text,
        DateTimeOffset at,
        string id) =>
        new(
            id,
            sessionId,
            at,
            [new TextPart(text)],
            StopReason.Stop,
            new Usage(1, 1),
            "claude-opus-4");

    private static ToolResultMessage ToolResult(
        string sessionId,
        string output,
        DateTimeOffset at,
        string id) =>
        new(id, sessionId, at, [new ToolResultEntry("tc-1", "read", output, false)]);

    private static async Task<Session> SeedAsync(SqliteSessionStore store, string directory = "/proj")
    {
        var created = await store.CreateAsync(directory, "code", "anthropic", "claude-opus-4");
        await Assert.That(created).IsSuccessful();
        return created.Value;
    }

    [Test]
    public async Task QueryAsync_TextMatch_FindsTheProseOfEveryMessageKind()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "the quick brown fox", BaseInstant, "m-user"));
            await store.AppendMessageAsync(
                session.Id,
                Assistant(session.Id, "a brown dog jumps", BaseInstant.AddMinutes(1), "m-assistant"));
            await store.AppendMessageAsync(
                session.Id,
                ToolResult(session.Id, "the file says brown", BaseInstant.AddMinutes(2), "m-tool"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "brown",
                RecordKinds = [SessionRecordKind.Message]
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(3);
            await Assert.That(result.Value.TotalMatches).IsEqualTo(3);
            await Assert.That(result.Value.Truncated).IsFalse();

            // Every kind of message contributes its prose — the index projects
            // the user prompt, the assistant's text parts and the tool output.
            var ids = new List<string>();
            foreach (SessionQueryHit hit in result.Value.Hits)
                ids.Add(hit.RecordId);
            await Assert.That(ids.Contains("m-user")).IsTrue();
            await Assert.That(ids.Contains("m-assistant")).IsTrue();
            await Assert.That(ids.Contains("m-tool")).IsTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_TextMatch_UsesTheFullTextIndexWhenTheBuildHasFts5()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "the quick brown fox", BaseInstant, "m-user"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions { Text = "brown" });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Mode).IsEqualTo(SessionQueryMode.FullText);
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_LiteralPath_MatchesSubstringsAndReportsThePosition()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "the quick brown fox", BaseInstant, "m-user"));

            // The literal path is the fallback a SQLite build without FTS5
            // takes; it is forced here so both paths are covered whatever the
            // bundled SQLite ships.
            var query = CreateQuery(dbPath, allowFullText: false);
            var result = await query.QueryAsync(new SessionQueryOptions { Text = "uick br" });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Mode).IsEqualTo(SessionQueryMode.Literal);
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            // "uick br" starts at offset 5 of "the quick brown fox".
            await Assert.That(result.Value.Hits[0].MatchPosition).IsEqualTo(5);
            await Assert.That(result.Value.Hits[0].Text).IsEqualTo("the quick brown fox");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_RoleFilter_SelectsOnlyThatRole()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-user"));
            await store.AppendMessageAsync(
                session.Id,
                Assistant(session.Id, "alpha", BaseInstant.AddMinutes(1), "m-assistant"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Roles = ["assistant"]
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].Role).IsEqualTo("assistant");
            await Assert.That(result.Value.Hits[0].RecordId).IsEqualTo("m-assistant");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_SessionIdFilter_SelectsOnlyThatSession()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session first = await SeedAsync(store, "/proj-a");
            Session second = await SeedAsync(store, "/proj-b");
            await store.AppendMessageAsync(
                first.Id,
                User(first.Id, "alpha", BaseInstant, "m-first"));
            await store.AppendMessageAsync(
                second.Id,
                User(second.Id, "alpha", BaseInstant, "m-second"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                SessionIds = [first.Id]
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].SessionId).IsEqualTo(first.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_Window_SelectsOnlyTheRecordsInsideIt()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-first"));
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant.AddHours(2), "m-second"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Window = new SessionQueryWindow(BaseInstant, BaseInstant.AddHours(1))
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].RecordId).IsEqualTo("m-first");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_Limit_TruncatesAndReportsTheTotal()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            for (int i = 0; i < 5; i++)
            {
                await store.AppendMessageAsync(
                    session.Id,
                    User(session.Id, "alpha", BaseInstant.AddMinutes(i), $"m-{i}"));
            }

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Limit = 2
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(2);
            await Assert.That(result.Value.TotalMatches).IsEqualTo(5);
            await Assert.That(result.Value.Truncated).IsTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_Offset_SkipsTheFirstHits()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-first"));
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant.AddMinutes(1), "m-second"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Rank = SessionQueryRank.OldestFirst,
                Offset = 1,
                Limit = 5
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].RecordId).IsEqualTo("m-second");
            await Assert.That(result.Value.TotalMatches).IsEqualTo(2);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_Rank_OrdersChronologically()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant.AddMinutes(2), "m-late"));
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-early"));

            var query = CreateQuery(dbPath);

            var oldest = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Rank = SessionQueryRank.OldestFirst
            });
            await Assert.That(oldest).IsSuccessful();
            await Assert.That(oldest.Value.Hits[0].RecordId).IsEqualTo("m-early");

            var newest = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Rank = SessionQueryRank.NewestFirst
            });
            await Assert.That(newest).IsSuccessful();
            await Assert.That(newest.Value.Hits[0].RecordId).IsEqualTo("m-late");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_Relevance_OrdersByScore()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-user"));
            await store.AppendMessageAsync(
                session.Id,
                Assistant(session.Id, "alpha and alpha and alpha", BaseInstant.AddMinutes(1), "m-many"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                Rank = SessionQueryRank.Relevance
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(2);
            // The hit with more occurrences of the term is the better match.
            await Assert.That(result.Value.Hits[0].Score)
                .IsGreaterThanOrEqualTo(result.Value.Hits[1].Score);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_EmptySessionIdList_MatchesNothing()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-user"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                SessionIds = []
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits).IsEmpty();
            await Assert.That(result.Value.TotalMatches).IsEqualTo(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_InvalidOptions_FailsWithoutTouchingTheDatabase()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            await SeedAsync(store);

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions { Limit = 0 });

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("limit");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_EventKind_IsNotServedByThisBackend()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-user"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "alpha",
                RecordKinds = [SessionRecordKind.Event]
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits).IsEmpty();
            await Assert.That(result.Value.SupportedKinds.Contains(SessionRecordKind.Event)).IsFalse();
            await Assert.That(result.Value.SupportedKinds.Contains(SessionRecordKind.Message)).IsTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_SessionKind_FindsTheHeaderByTitle()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            var titled = session with { Title = "refactor the query service" };
            await store.UpdateAsync(titled);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "unrelated", BaseInstant, "m-user"));

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions
            {
                Text = "query service",
                RecordKinds = [SessionRecordKind.Session]
            });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].Kind).IsEqualTo(SessionRecordKind.Session);
            await Assert.That(result.Value.Hits[0].RecordId).IsEqualTo(session.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_CorruptRow_IsSkippedNotFatal()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-good"));

            // A row whose payload is not JSON must not take the whole query
            // down: it is skipped, exactly as the store skips an unreadable
            // row when it reads a history back.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO messages (id, session_id, role, created_at, created_at_ms, payload)
                    VALUES ('m-broken', @sid, 'user', '2026-10-20T10:00:00.0000000+00:00', 1, '{not json')
                    """;
                cmd.Parameters.AddWithValue("@sid", session.Id);
                cmd.ExecuteNonQuery();
            }

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions { Text = "alpha" });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].RecordId).IsEqualTo("m-good");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Test]
    public async Task QueryAsync_MissingDatabase_AnswersEmpty()
    {
        string dbPath = Path.Combine(
            Path.GetTempPath(),
            $"harbor-sqlite-query-missing-{Guid.NewGuid():N}.db");

        var query = CreateQuery(dbPath);
        var result = await query.QueryAsync(new SessionQueryOptions { Text = "alpha" });

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Hits).IsEmpty();
        await Assert.That(result.Value.TotalMatches).IsEqualTo(0);
        await Assert.That(result.Value.Truncated).IsFalse();
    }

    [Test]
    public async Task QueryAsync_DeletedMessage_LeavesTheIndex()
    {
        var store = CreateStore(out string dbPath);
        try
        {
            Session session = await SeedAsync(store);
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant, "m-first"));
            await store.AppendMessageAsync(
                session.Id,
                User(session.Id, "alpha", BaseInstant.AddMinutes(1), "m-second"));

            var deleted = await store.DeleteMessagesAfterAsync(session.Id, "m-first");
            await Assert.That(deleted).IsSuccessful();

            var query = CreateQuery(dbPath);
            var result = await query.QueryAsync(new SessionQueryOptions { Text = "alpha" });

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.Hits.Count).IsEqualTo(1);
            await Assert.That(result.Value.Hits[0].RecordId).IsEqualTo("m-first");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }
}
