// SqliteSessionQueryIndex.cs — the read-side full-text index of the session log (#1743).
//
// WHY THE INDEX LIVES HERE AND NOT IN THE STORE
// ---------------------------------------------
// The store (SqliteSessionStore) owns the WRITE side; this is a READ-side
// projection of the same rows. Keeping it out of the store means a deployment
// that never queries pays nothing for it, and a deployment whose SQLite build
// has no FTS5 keeps working through the literal path instead of failing.
//
// WHY TRIGGERS
// -------------
// An index that can drift from the message table returns WRONG answers, which
// is worse than no index at all: a stale hit is indistinguishable from a
// current one. SQLite's own answer to "keep this projection in sync" is a
// trigger per write path (insert / update / delete) on the message table plus
// a one-shot backfill for the rows that predate the index. The triggers are
// the whole sync story — there is no watermark, no rebuild-on-read and no
// window in which the index and the table disagree.
//
// WHY THE TEXT IS PROJECTED IN SQL
// --------------------------------
// The searchable text of a message is its prose: the user's prompt, the
// assistant's text parts, the tool outputs. The JSON payload carries it, and
// the projection below reads it out of the payload with the JSON functions
// SQLite ships (json_extract / json_each). Doing it in SQL keeps the store's
// write path untouched — no extra column, no migration of the message table —
// and keeps the index and the fallback scan reading the SAME expression, so
// the two paths cannot disagree about what a message says.
//
// The price is honest and worth naming: the expression knows the payload
// shape, so a shape change must bump IndexVersion (the index is then rebuilt)
// — and the integration test writes one message of every kind and asserts the
// full-text path finds their text, which is what catches the drift.

using Microsoft.Data.Sqlite;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     The FTS5 index behind <see cref="SqliteSessionQuery" />: the searchable
///     text of every persisted message, kept in sync with the message table
///     by triggers and read with a BM25-ranked MATCH query.
/// </summary>
internal static class SqliteSessionQueryIndex
{
    /// <summary>The FTS5 virtual table holding the searchable text of the message log.</summary>
    internal const string FtsTable = "session_query_fts";

    /// <summary>The bookkeeping table holding the index version.</summary>
    internal const string MetaTable = "session_query_meta";

    /// <summary>The meta key the index version is stored under.</summary>
    internal const string VersionKey = "index_version";

    /// <summary>
    ///     The version of the index projection. Bump it when the extraction
    ///     changes: the index is then dropped and rebuilt from the message
    ///     table on the next query.
    /// </summary>
    internal const string Version = "1";

    private static readonly Lazy<bool> Fts5Available =
        new(ProbeFts5, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string TriggerText = TextExpression("new");
    private static readonly string RowText = TextExpression("m");

    /// <summary>
    ///     Whether the SQLite build this process loaded has FTS5 compiled in.
    ///     The bundled e_sqlite3 does; a system SQLite without FTS5 does not,
    ///     and the answer is probed rather than assumed.
    /// </summary>
    internal static bool IsFts5Available => Fts5Available.Value;

    /// <summary>
    ///     The searchable text of one message row, projected from its JSON
    ///     payload.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The projection mirrors what <c>harbor sessions search</c> reads
    ///         today: the user's prompt, the assistant's TEXT parts (thinking
    ///         and tool calls are not prose) and the tool outputs. A payload
    ///         that is not valid JSON yields an empty text rather than an
    ///         error — a corrupt row must not break the write that carried it.
    ///     </para>
    /// </remarks>
    /// <param name="row">
    ///     The row alias the expression is projected for — <c>new</c> inside a
    ///     trigger, <c>m</c> for the message table.
    /// </param>
    /// <returns>The SQL expression yielding the row's searchable text.</returns>
    internal static string TextExpression(string row) => $"""
        CASE
          WHEN {row}.role = 'user' AND json_valid({row}.payload)
            THEN COALESCE(json_extract({row}.payload, '$.content'), '')
          WHEN {row}.role = 'assistant' AND json_valid({row}.payload)
            THEN COALESCE((SELECT group_concat(json_extract(p.value, '$.text'), ' ')
                           FROM json_each({row}.payload, '$.parts') AS p
                           WHERE json_extract(p.value, '$.type') = 'text'), '')
          WHEN {row}.role = 'tool_result' AND json_valid({row}.payload)
            THEN COALESCE((SELECT group_concat(json_extract(r.value, '$.output'), ' ')
                           FROM json_each({row}.payload, '$.results') AS r), '')
          ELSE ''
        END
        """;

    /// <summary>
    ///     The DDL that creates the index and the triggers that keep it in
    ///     sync with the message table.
    /// </summary>
    /// <returns>The DDL script.</returns>
    internal static string CreateDdl() => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTable} USING fts5(
            text,
            record_id UNINDEXED,
            session_id UNINDEXED,
            role UNINDEXED,
            created_at_ms UNINDEXED
        );
        CREATE TRIGGER IF NOT EXISTS session_query_fts_ai AFTER INSERT ON messages BEGIN
          INSERT INTO {FtsTable} (rowid, text, record_id, session_id, role, created_at_ms)
          VALUES (new.rowid, {TriggerText}, new.id, new.session_id, new.role, new.created_at_ms);
        END;
        CREATE TRIGGER IF NOT EXISTS session_query_fts_au AFTER UPDATE ON messages BEGIN
          DELETE FROM {FtsTable} WHERE rowid = old.rowid;
          INSERT INTO {FtsTable} (rowid, text, record_id, session_id, role, created_at_ms)
          VALUES (new.rowid, {TriggerText}, new.id, new.session_id, new.role, new.created_at_ms);
        END;
        CREATE TRIGGER IF NOT EXISTS session_query_fts_ad AFTER DELETE ON messages BEGIN
          DELETE FROM {FtsTable} WHERE rowid = old.rowid;
        END;
        """;

    /// <summary>
    ///     The one-shot backfill: index every message row the index does not
    ///     hold yet (the rows written before the index existed).
    /// </summary>
    /// <returns>The backfill statement.</returns>
    internal static string BackfillSql() => $"""
        INSERT INTO {FtsTable} (rowid, text, record_id, session_id, role, created_at_ms)
        SELECT m.rowid, {RowText}, m.id, m.session_id, m.role, m.created_at_ms
        FROM messages m
        WHERE NOT EXISTS (SELECT 1 FROM {FtsTable} f WHERE f.rowid = m.rowid)
        """;

    /// <summary>
    ///     Make sure the index exists and is current.
    /// </summary>
    /// <remarks>
    ///     The check is two cheap catalog reads: the index is rebuilt only when
    ///     it is missing or its version moved (the extraction changed).
    /// </remarks>
    /// <param name="conn">An open connection to the session database.</param>
    /// <returns>
    ///     True when the full-text path is available; false when this SQLite
    ///     build has no FTS5 or the database has no message table yet — the
    ///     caller then serves the query through the literal path.
    /// </returns>
    internal static bool Ensure(SqliteConnection conn)
    {
        if (!IsFts5Available)
            return false;
        if (!TableExists(conn, "messages"))
            return false;

        if (string.Equals(ReadVersion(conn), Version, StringComparison.Ordinal) && TableExists(conn, FtsTable))
            return true;

        Rebuild(conn);
        return true;
    }

    /// <summary>
    ///     Drop the index and build it again from the message table. The
    ///     rebuild runs in one immediate transaction, so a concurrent writer
    ///     cannot slip a row between the drop and the backfill.
    /// </summary>
    private static void Rebuild(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction(deferred: false);

        using (var reset = conn.CreateCommand())
        {
            reset.Transaction = tx;
            reset.CommandText = $"""
                DROP TRIGGER IF EXISTS session_query_fts_ai;
                DROP TRIGGER IF EXISTS session_query_fts_au;
                DROP TRIGGER IF EXISTS session_query_fts_ad;
                DROP TABLE IF EXISTS {FtsTable};
                CREATE TABLE IF NOT EXISTS {MetaTable} (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            reset.ExecuteNonQuery();
        }

        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = CreateDdl();
            create.ExecuteNonQuery();
        }

        using (var backfill = conn.CreateCommand())
        {
            backfill.Transaction = tx;
            backfill.CommandText = BackfillSql();
            backfill.ExecuteNonQuery();
        }

        using (var stamp = conn.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = $"INSERT OR REPLACE INTO {MetaTable} (key, value) VALUES (@key, @value)";
            stamp.Parameters.AddWithValue("@key", VersionKey);
            stamp.Parameters.AddWithValue("@value", Version);
            stamp.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Whether a table (or virtual table) of that name exists.</summary>
    internal static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        object? scalar = cmd.ExecuteScalar();
        return scalar is long count && count > 0;
    }

    /// <summary>The recorded index version, or null when the index is unknown.</summary>
    private static string? ReadVersion(SqliteConnection conn)
    {
        if (!TableExists(conn, MetaTable))
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT value FROM {MetaTable} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", VersionKey);
        object? scalar = cmd.ExecuteScalar();
        return scalar as string;
    }

    /// <summary>
    ///     Probe the SQLite build for FTS5 by creating a throwaway virtual
    ///     table in an in-memory database. The probe is the honest answer:
    ///     whether FTS5 exists is a property of the bundled native library, not
    ///     of this code.
    /// </summary>
    private static bool ProbeFts5()
    {
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE VIRTUAL TABLE temp.fts5_probe USING fts5(text)";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
