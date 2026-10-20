// SqliteSessionQuery.cs — the SQLite read side of the session log (#1743).
//
// WHAT THIS IS
// ------------
// The store (SqliteSessionStore) answers EXACT reads: one session, one
// message list. A search is not an exact read, and answering it through the
// store meant reading every message of every session and scanning the bytes
// (apps/Harbor.App.Cli/Commands/SessionSearchRunner.cs). This class is the
// other half: one query surface where the FILTERS reach the storage engine,
// the text filter is served by an index where the engine has one, and the
// result comes back ranked and bounded.
//
// THE TWO TEXT PATHS
// ------------------
//   * full-text: an FTS5 MATCH over the index SqliteSessionQueryIndex keeps,
//     ranked by BM25. Token semantics — a query is a phrase of tokens.
//   * literal:   a LIKE scan over the same projection, ranked by the position
//     of the first match. Substring semantics.
//
// Which path ran is reported in SessionQueryResult.Mode: a caller that needs
// substring semantics (a path fragment, an identifier) cannot mistake a
// token match for one, and a caller that needs relevance knows it has it.
//
// WHAT IS NOT HERE YET
// --------------------
//   * The event layer. No backend persists agent events yet, so the Event
//     kind is not in SupportedKinds and an event filter matches nothing.
//     The vocabulary is in the contract; the storage is the next slice.
//   * The JSONL backend. This class is the SQLite implementation of the
//     contract the JSONL backend will implement the same way.

using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     The SQLite implementation of <see cref="ISessionQuery" />: filters and
///     the time window are pushed into SQL, the text filter is served by an
///     FTS5 index when the SQLite build has one and by a literal scan when it
///     does not, and the hits come back ranked and bounded.
/// </summary>
public sealed class SqliteSessionQuery : ISessionQuery
{
    /// <summary>
    ///     The record kinds this backend can serve. The event kind is absent:
    ///     no SQLite table holds agent events yet, and a kind that is missing
    ///     here was NOT searched — which is not the same as "nothing matched".
    /// </summary>
    private static readonly IReadOnlyList<SessionRecordKind> SupportedKinds =
        new[] { SessionRecordKind.Session, SessionRecordKind.Message };

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionQuery> _logger;
    private readonly bool _allowFullText;

    /// <summary>
    ///     Create the query surface over a session database.
    /// </summary>
    /// <param name="dbPath">Path of the SQLite session database.</param>
    /// <param name="logger">Logger for the query's own diagnostics.</param>
    public SqliteSessionQuery(string dbPath, ILogger<SqliteSessionQuery> logger)
        : this(dbPath, logger, allowFullText: true)
    {
    }

    /// <summary>
    ///     Create the query surface, optionally without the full-text path.
    /// </summary>
    /// <param name="dbPath">Path of the SQLite session database.</param>
    /// <param name="logger">Logger for the query's own diagnostics.</param>
    /// <param name="allowFullText">
    ///     False forces the literal scan — the path a SQLite build without
    ///     FTS5 takes. It exists so both paths are covered by the tests
    ///     whatever the bundled SQLite ships.
    /// </param>
    internal SqliteSessionQuery(string dbPath, ILogger<SqliteSessionQuery> logger, bool allowFullText)
    {
        _dbPath = dbPath;
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        _allowFullText = allowFullText;
    }

    /// <summary>
    ///     Run the query.
    /// </summary>
    /// <param name="options">The query: filters, window, text, limit and rank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The matched records, or a failure when the query is invalid or the
    ///     database could not answer it.
    /// </returns>
    public async Task<Result<SessionQueryResult>> QueryAsync(
        SessionQueryOptions options,
        CancellationToken ct = default)
    {
        var validation = options.Validate();
        if (validation.IsFailure)
            return validation.ConvertFailure<SessionQueryResult>();

        Result<SessionQueryResult> result = await Result.Try(
            async () =>
            {
                return await RunAsync(options, ct).ConfigureAwait(false);
            },
            ResultErrors.Message).ConfigureAwait(false);

        return result.TapError(error => SqliteSessionQueryLog.QueryFailed(_logger, error));
    }

    /// <summary>
    ///     The query itself. Every expected state is a value, not an
    ///     exception: a missing database, a database without the session
    ///     tables and an empty filter list all answer with an empty result.
    /// </summary>
    private async Task<SessionQueryResult> RunAsync(SessionQueryOptions options, CancellationToken ct)
    {
        if (!File.Exists(_dbPath))
            return Empty(SessionQueryMode.Literal);

        using var conn = OpenConnection();

        bool hasMessages = SqliteSessionQueryIndex.TableExists(conn, "messages");
        bool hasSessions = SqliteSessionQueryIndex.TableExists(conn, "sessions");

        // The index is only built when a text query needs it: a filter-only
        // query must not turn a read into a write.
        bool fullTextAvailable = false;
        if (hasMessages && _allowFullText && options.Text is not null)
        {
            try
            {
                fullTextAvailable = SqliteSessionQueryIndex.Ensure(conn);
            }
            catch (SqliteException)
            {
                // The index could not be built (a read-only database, a
                // locked file). The literal path answers the same question.
                SqliteSessionQueryLog.FullTextUnavailable(_logger);
            }
        }

        var hits = new List<SessionQueryHit>();
        int total = 0;
        SessionQueryMode mode = SessionQueryMode.Literal;

        if (hasMessages && WantsKind(options, SessionRecordKind.Message) && !HasEmptyMessageFilter(options))
        {
            Branch branch;
            if (fullTextAvailable && options.Text is not null)
            {
                try
                {
                    branch = await ReadMessagesFullTextAsync(conn, options, ct).ConfigureAwait(false);
                }
                catch (SqliteException)
                {
                    // The index exists but the MATCH could not be served (a
                    // query the tokenizer rejects, a damaged index). The
                    // literal path answers the same question with substring
                    // semantics — a worse ranking, but an answer.
                    SqliteSessionQueryLog.FullTextUnavailable(_logger);
                    branch = await ReadMessagesLiteralAsync(conn, options, ct).ConfigureAwait(false);
                }
            }
            else
            {
                branch = await ReadMessagesLiteralAsync(conn, options, ct).ConfigureAwait(false);
            }

            hits.AddRange(branch.Hits);
            total += branch.Total;
            mode = branch.Mode;
        }
        if (hasSessions && WantsKind(options, SessionRecordKind.Session) && !HasEmptySessionFilter(options))
        {
            Branch branch = await ReadSessionsAsync(conn, options, ct).ConfigureAwait(false);
            hits.AddRange(branch.Hits);
            total += branch.Total;
        }

        // Every branch returns its own top (limit + offset) hits; the merge
        // then skips the offset once and cuts to the limit. Fetching
        // limit + offset per branch is what makes the merged page correct: a
        // row outside its branch's top (limit + offset) cannot be in the
        // query's top (limit + offset).
        List<SessionQueryHit> ordered = Order(hits, options.Rank);
        List<SessionQueryHit> page = ordered.Skip(options.Offset).Take(options.Limit).ToList();

        return new SessionQueryResult(
            page,
            total,
            total > options.Offset + page.Count,
            mode,
            SupportedKinds);
    }

    /// <summary>
    ///     The message branch over the FTS5 index: a MATCH query filtered in
    ///     SQL and ranked by BM25.
    /// </summary>
    private async Task<Branch> ReadMessagesFullTextAsync(
        SqliteConnection conn,
        SessionQueryOptions options,
        CancellationToken ct)
    {
        if (options.Text is not { } query)
            return new Branch(Array.Empty<SessionQueryHit>(), 0, SessionQueryMode.FullText);

        string phrase = MatchPhrase(query);
        int take = options.Limit + options.Offset;

        int total;
        using (var countCmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append("SELECT count(*) FROM session_query_fts WHERE session_query_fts MATCH @match");
            AppendFilters(
                sb,
                countCmd,
                options,
                "session_query_fts.session_id",
                "session_query_fts.role",
                "session_query_fts.created_at_ms");
            countCmd.CommandText = sb.ToString();
            countCmd.Parameters.AddWithValue("@match", phrase);
            object? scalar = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = scalar is long count ? (int)count : 0;
        }

        var hits = new List<SessionQueryHit>();
        using (var cmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append("SELECT session_query_fts.record_id, session_query_fts.session_id, session_query_fts.role, ");
            sb.Append("session_query_fts.created_at_ms, session_query_fts.text, bm25(session_query_fts) AS score ");
            sb.Append("FROM session_query_fts WHERE session_query_fts MATCH @match");
            AppendFilters(
                sb,
                cmd,
                options,
                "session_query_fts.session_id",
                "session_query_fts.role",
                "session_query_fts.created_at_ms");
            sb.Append(' ').Append(OrderByIndex(options.Rank));
            sb.Append(" LIMIT @take");
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@match", phrase);
            cmd.Parameters.AddWithValue("@take", take);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                hits.Add(new SessionQueryHit(
                    SessionRecordKind.Message,
                    reader.GetString(1),
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    -Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                    -1,
                    reader.GetString(2)));
            }
        }

        return new Branch(hits, total, SessionQueryMode.FullText);
    }

    /// <summary>
    ///     The message branch over the message table itself: a literal scan
    ///     ranked by the position of the first match. This is the path taken
    ///     when the SQLite build has no FTS5, and the path taken when the
    ///     query carries no text at all (nothing to match).
    /// </summary>
    private async Task<Branch> ReadMessagesLiteralAsync(
        SqliteConnection conn,
        SessionQueryOptions options,
        CancellationToken ct)
    {
        string text = SqliteSessionQueryIndex.TextExpression("m");
        int take = options.Limit + options.Offset;

        int total;
        using (var countCmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append("SELECT count(*) FROM messages m WHERE 1=1");
            AppendTextCondition(sb, countCmd, options, text);
            AppendFilters(sb, countCmd, options, "m.session_id", "m.role", "m.created_at_ms");
            countCmd.CommandText = sb.ToString();
            object? scalar = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = scalar is long count ? (int)count : 0;
        }

        var hits = new List<SessionQueryHit>();
        using (var cmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append("SELECT m.id, m.session_id, m.role, m.created_at_ms, ");
            sb.Append(text).Append(" AS hit_text, instr(lower(").Append(text).Append("), lower(@probe)) AS pos ");
            sb.Append("FROM messages m WHERE 1=1");
            AppendTextCondition(sb, cmd, options, text);
            AppendFilters(sb, cmd, options, "m.session_id", "m.role", "m.created_at_ms");
            sb.Append(' ').Append(OrderByMessages(options.Rank));
            sb.Append(" LIMIT @take");
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@probe", options.Text is null ? DBNull.Value : options.Text);
            cmd.Parameters.AddWithValue("@take", take);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                int position = ReadPosition(reader, 5);
                hits.Add(new SessionQueryHit(
                    SessionRecordKind.Message,
                    reader.GetString(1),
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    position < 0 ? 0d : -position,
                    position,
                    reader.GetString(2)));
            }
        }

        return new Branch(hits, total, SessionQueryMode.Literal);
    }

    /// <summary>
    ///     The session branch: the session headers, filtered by id and by the
    ///     window (a session is filed under its last-activity instant).
    /// </summary>
    private async Task<Branch> ReadSessionsAsync(
        SqliteConnection conn,
        SessionQueryOptions options,
        CancellationToken ct)
    {
        int take = options.Limit + options.Offset;

        int total;
        using (var countCmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append("SELECT count(*) FROM sessions s WHERE 1=1");
            AppendTextCondition(sb, countCmd, options, "s.title");
            AppendFilters(sb, countCmd, options, "s.id", null, "unixepoch(s.updated_at) * 1000");
            countCmd.CommandText = sb.ToString();
            object? scalar = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = scalar is long count ? (int)count : 0;
        }

        var hits = new List<SessionQueryHit>();
        using (var cmd = conn.CreateCommand())
        {
            var sb = new StringBuilder();
            sb.Append("SELECT s.id, s.title, unixepoch(s.updated_at) * 1000 AS ts, ");
            sb.Append("instr(lower(s.title), lower(@probe)) AS pos FROM sessions s WHERE 1=1");
            AppendTextCondition(sb, cmd, options, "s.title");
            AppendFilters(sb, cmd, options, "s.id", null, "unixepoch(s.updated_at) * 1000");
            sb.Append(' ').Append(OrderBySessions(options.Rank));
            sb.Append(" LIMIT @take");
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@probe", options.Text is null ? DBNull.Value : options.Text);
            cmd.Parameters.AddWithValue("@take", take);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                int position = ReadPosition(reader, 3);
                hits.Add(new SessionQueryHit(
                    SessionRecordKind.Session,
                    reader.GetString(0),
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    position < 0 ? 0d : -position,
                    position));
            }
        }

        return new Branch(hits, total, SessionQueryMode.Literal);
    }

    /// <summary>
    ///     Append the text condition of a branch. A query without text has no
    ///     condition; a query with text matches the projected text as a
    ///     substring (the literal path's semantics).
    /// </summary>
    private static void AppendTextCondition(
        StringBuilder sb,
        SqliteCommand cmd,
        SessionQueryOptions options,
        string textColumn)
    {
        if (options.Text is not { } query)
            return;

        sb.Append(" AND ").Append(textColumn).Append(" LIKE @pattern ESCAPE '\\'");
        cmd.Parameters.AddWithValue("@pattern", LikePattern(query));
    }

    /// <summary>
    ///     Append the session-id, role and window clauses of a branch.
    /// </summary>
    /// <param name="sb">The statement being built.</param>
    /// <param name="cmd">The command the clause's parameters are bound on.</param>
    /// <param name="options">The query options.</param>
    /// <param name="sessionColumn">The session-id column of this branch.</param>
    /// <param name="roleColumn">
    ///     The role column of this branch, or null for a branch that has no
    ///     roles — the session header has none, and the role clause applies to
    ///     message records only.
    /// </param>
    /// <param name="timestampColumn">
    ///     The timestamp column of this branch, in Unix milliseconds.
    /// </param>
    private static void AppendFilters(
        StringBuilder sb,
        SqliteCommand cmd,
        SessionQueryOptions options,
        string sessionColumn,
        string? roleColumn,
        string timestampColumn)
    {
        if (options.SessionIds is { } ids)
        {
            sb.Append(" AND ").Append(sessionColumn).Append(" IN (");
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                string name = $"@sid{i}";
                sb.Append(name);
                cmd.Parameters.AddWithValue(name, ids[i]);
            }

            sb.Append(')');
        }

        if (options.Roles is { } roles && roleColumn is not null)
        {
            sb.Append(" AND ").Append(roleColumn).Append(" IN (");
            for (int i = 0; i < roles.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                string name = $"@role{i}";
                sb.Append(name);
                cmd.Parameters.AddWithValue(name, roles[i]);
            }

            sb.Append(')');
        }

        if (options.Window is { } window)
        {
            if (window.From.HasValue)
            {
                sb.Append(" AND ").Append(timestampColumn).Append(" >= @from");
                cmd.Parameters.AddWithValue("@from", window.From.Value.ToUnixTimeMilliseconds());
            }

            if (window.To.HasValue)
            {
                sb.Append(" AND ").Append(timestampColumn).Append(" <= @to");
                cmd.Parameters.AddWithValue("@to", window.To.Value.ToUnixTimeMilliseconds());
            }
        }
    }

    /// <summary>
    ///     Order the hits of one query. The relevance order reads the reported
    ///     score (higher is better); the chronological orders read the
    ///     timestamp. The record id breaks every tie, so two runs of the same
    ///     query return the same page.
    /// </summary>
    private static List<SessionQueryHit> Order(List<SessionQueryHit> hits, SessionQueryRank rank) => rank switch
    {
        SessionQueryRank.Relevance => hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Timestamp)
            .ThenBy(hit => hit.RecordId, StringComparer.Ordinal)
            .ToList(),
        SessionQueryRank.OldestFirst => hits
            .OrderBy(hit => hit.Timestamp)
            .ThenBy(hit => hit.RecordId, StringComparer.Ordinal)
            .ToList(),
        SessionQueryRank.NewestFirst => hits
            .OrderByDescending(hit => hit.Timestamp)
            .ThenBy(hit => hit.RecordId, StringComparer.Ordinal)
            .ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, "unmapped session query rank"),
    };

    /// <summary>The order of the indexed (full-text) message branch.</summary>
    private static string OrderByIndex(SessionQueryRank rank) => rank switch
    {
        SessionQueryRank.Relevance =>
            "ORDER BY score ASC, session_query_fts.created_at_ms ASC, session_query_fts.rowid ASC",
        SessionQueryRank.OldestFirst =>
            "ORDER BY session_query_fts.created_at_ms ASC, session_query_fts.rowid ASC",
        SessionQueryRank.NewestFirst =>
            "ORDER BY session_query_fts.created_at_ms DESC, session_query_fts.rowid ASC",
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, "unmapped session query rank"),
    };

    /// <summary>The order of the literal message branch.</summary>
    private static string OrderByMessages(SessionQueryRank rank) => rank switch
    {
        SessionQueryRank.Relevance => "ORDER BY pos ASC, m.created_at_ms ASC, m.rowid ASC",
        SessionQueryRank.OldestFirst => "ORDER BY m.created_at_ms ASC, m.rowid ASC",
        SessionQueryRank.NewestFirst => "ORDER BY m.created_at_ms DESC, m.rowid ASC",
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, "unmapped session query rank"),
    };

    /// <summary>The order of the session branch.</summary>
    private static string OrderBySessions(SessionQueryRank rank) => rank switch
    {
        SessionQueryRank.Relevance => "ORDER BY pos ASC, ts ASC, s.rowid ASC",
        SessionQueryRank.OldestFirst => "ORDER BY ts ASC, s.rowid ASC",
        SessionQueryRank.NewestFirst => "ORDER BY ts DESC, s.rowid ASC",
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, "unmapped session query rank"),
    };

    /// <summary>
    ///     The 0-based offset of the first text match inside the hit's text,
    ///     or -1 when the branch could not report one.
    /// </summary>
    /// <param name="reader">The reader positioned on the hit's row.</param>
    /// <param name="ordinal">The column holding the 1-based match offset.</param>
    private static int ReadPosition(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return -1;

        long position = reader.GetInt64(ordinal);
        return position <= 0 ? -1 : (int)(position - 1);
    }

    /// <summary>
    ///     The FTS5 phrase for a query text: the text as a quoted string, with
    ///     an embedded quote doubled. The quoting is what makes an arbitrary
    ///     user text a phrase instead of FTS5 syntax — a bare
    ///     <c>AND</c>, a stray <c>*</c> or a colon in the query must not be
    ///     able to change the shape of the statement.
    /// </summary>
    private static string MatchPhrase(string text) =>
        "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    ///     The LIKE pattern for a query text: the text as a substring, with the
    ///     LIKE wildcards of the text itself escaped.
    /// </summary>
    private static string LikePattern(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('%');
        foreach (char c in text)
        {
            if (c is '%' or '_' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }

        sb.Append('%');
        return sb.ToString();
    }

    /// <summary>Whether the query selects the given kind of record.</summary>
    private static bool WantsKind(SessionQueryOptions options, SessionRecordKind kind) =>
        options.RecordKinds is null || options.RecordKinds.Contains(kind);

    /// <summary>
    ///     Whether the message branch is ruled out by an empty filter value
    ///     list. The filter algebra of the contract: an empty list means
    ///     "nothing matches", not "no filter". The role clause constrains
    ///     message records only — a session header has no role and is not
    ///     ruled out by it.
    /// </summary>
    private static bool HasEmptyMessageFilter(SessionQueryOptions options) =>
        options.SessionIds is { Count: 0 }
        || options.Roles is { Count: 0 }
        || options.RecordKinds is { Count: 0 };

    /// <summary>
    ///     Whether the session branch is ruled out by an empty filter value
    ///     list. The session-id clause constrains session records; the role
    ///     clause does not.
    /// </summary>
    private static bool HasEmptySessionFilter(SessionQueryOptions options) =>
        options.SessionIds is { Count: 0 }
        || options.RecordKinds is { Count: 0 };

    /// <summary>The empty answer: no hits, nothing truncated, nothing matched.</summary>
    private static SessionQueryResult Empty(SessionQueryMode mode) =>
        new(Array.Empty<SessionQueryHit>(), 0, false, mode, SupportedKinds);

    /// <summary>Open a connection to the session database.</summary>
    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                """;
            pragma.ExecuteNonQuery();
        }

        return conn;
    }

    /// <summary>
    ///     One branch of the query: the hits it produced, how many records
    ///     matched in total, and how the text was matched.
    /// </summary>
    private readonly record struct Branch(
        IReadOnlyList<SessionQueryHit> Hits,
        int Total,
        SessionQueryMode Mode);
}

/// <summary>
///     BCL <c>[LoggerMessage]</c> delegates for <see cref="SqliteSessionQuery" />.
/// </summary>
internal static partial class SqliteSessionQueryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Session query failed: {Error}")]
    public static partial void QueryFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Full-text session query fell back to the literal scan")]
    public static partial void FullTextUnavailable(ILogger logger);
}
