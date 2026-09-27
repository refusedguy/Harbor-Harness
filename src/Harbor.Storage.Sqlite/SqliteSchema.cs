// SqliteSchema.cs — database schema + pre-#85 migrations for the SQLite store.
//
// Extracted verbatim from SqliteSessionStore.cs (#184 god-object
// decomposition). The store owns connection lifetime + per-session locking;
// this file owns DDL: the CREATE TABLE/INDEX statements and the migration
// that reshapes databases created before #85.

using Microsoft.Data.Sqlite;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     SQLite DDL for session storage: session/message tables, ordering
///     indexes, and the migration from the pre-#85 layout (global message PK,
///     missing integer ordering stamp).
/// </summary>
internal static class SqliteSchema
{
    internal const string Schema = """
                                   CREATE TABLE IF NOT EXISTS sessions (
                                       id TEXT PRIMARY KEY,
                                       project_id TEXT NOT NULL,
                                       directory TEXT NOT NULL,
                                       title TEXT NOT NULL,
                                       agent TEXT NOT NULL,
                                       model TEXT NOT NULL,
                                       provider_id TEXT NOT NULL,
                                       version TEXT NOT NULL,
                                       created_at TEXT NOT NULL,
                                       updated_at TEXT NOT NULL,
                                       metadata TEXT NOT NULL
                                   );
                                   CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(project_id);
                                   CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at DESC);

                                   CREATE TABLE IF NOT EXISTS messages (
                                       id TEXT NOT NULL,
                                       session_id TEXT NOT NULL,
                                       parent_id TEXT,
                                       role TEXT NOT NULL,
                                       agent TEXT,
                                       model TEXT,
                                       created_at TEXT NOT NULL,
                                       created_at_ms INTEGER NOT NULL DEFAULT 0,
                                       payload TEXT NOT NULL,
                                       PRIMARY KEY (session_id, id),
                                       FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
                                   );
                                   CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id, created_at_ms);
                                   CREATE INDEX IF NOT EXISTS idx_messages_parent ON messages(parent_id);
                                   """;

    /// <summary>
    ///     Migrate databases created before #85:
    ///     (1) global PRIMARY KEY (id) → composite (session_id, id);
    ///     (2) missing created_at_ms integer stamp → add + backfill from text;
    ///     (3) ordering index rebuilt over the integer stamp.
    /// </summary>
    internal static void MigrateMessagesIfNeeded(SqliteConnection conn)
    {
        if (HasLegacyMessagePk(conn))
            RebuildMessagesTable(conn);

        if (!HasColumn(conn, "created_at_ms"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE messages ADD COLUMN created_at_ms INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
            BackfillCreatedAtMs(conn);
        }

        using var idx = conn.CreateCommand();
        idx.CommandText = """
                          DROP INDEX IF EXISTS idx_messages_session;
                          CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id, created_at_ms);
                          """;
        idx.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection conn, string column)
    {
        // S2077: PRAGMA takes no parameters — the table name is a hardcoded
        // internal constant, never user input.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(messages)";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasLegacyMessagePk(SqliteConnection conn)
    {
        var pkCols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(messages)";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(5) > 0)
                pkCols.Add(reader.GetString(1));
        }

        // Current shape: PRIMARY KEY (session_id, id). Anything else (notably
        // the pre-#85 global PRIMARY KEY (id)) needs a rebuild.
        return !(pkCols.Contains("session_id", StringComparer.OrdinalIgnoreCase)
            && pkCols.Contains("id", StringComparer.OrdinalIgnoreCase));
    }

    private static void BackfillCreatedAtMs(SqliteConnection conn)
    {
        var rows = new List<(string SessionId, string Id, string CreatedAt)>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT session_id, id, created_at FROM messages";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var (sessionId, id, createdAt) in rows)
        {
            long ms = DateTimeOffset.TryParse(createdAt, out var dto)
                ? dto.ToUnixTimeMilliseconds()
                : 0;
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE messages SET created_at_ms = @ms WHERE session_id = @sid AND id = @mid";
            upd.Parameters.AddWithValue("@ms", ms);
            upd.Parameters.AddWithValue("@sid", sessionId);
            upd.Parameters.AddWithValue("@mid", id);
            upd.ExecuteNonQuery();
        }
    }

    /// <summary>
    ///     Rebuild the messages table to the composite-PK shape, preserving
    ///     every row (timestamps re-stamped from created_at text).
    /// </summary>
    private static void RebuildMessagesTable(SqliteConnection conn)
    {
        var rows = new List<(string Id, string SessionId, string? ParentId, string Role, string? Agent, string? Model, string CreatedAt, string Payload)>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT id, session_id, parent_id, role, agent, model, created_at, payload FROM messages";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }

        using var tx = conn.BeginTransaction();
        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                                 CREATE TABLE messages_new (
                                     id TEXT NOT NULL,
                                     session_id TEXT NOT NULL,
                                     parent_id TEXT,
                                     role TEXT NOT NULL,
                                     agent TEXT,
                                     model TEXT,
                                     created_at TEXT NOT NULL,
                                     created_at_ms INTEGER NOT NULL DEFAULT 0,
                                     payload TEXT NOT NULL,
                                     PRIMARY KEY (session_id, id),
                                     FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
                                 )
                                 """;
            create.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            long ms = DateTimeOffset.TryParse(row.CreatedAt, out var dto)
                ? dto.ToUnixTimeMilliseconds()
                : 0;
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                              INSERT INTO messages_new (id, session_id, parent_id, role, agent, model, created_at, created_at_ms, payload)
                              VALUES (@id, @sid, @pid, @role, @agent, @model, @created, @createdMs, @payload)
                              """;
            ins.Parameters.AddWithValue("@id", row.Id);
            ins.Parameters.AddWithValue("@sid", row.SessionId);
            ins.Parameters.AddWithValue("@pid", (object?)row.ParentId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@role", row.Role);
            ins.Parameters.AddWithValue("@agent", (object?)row.Agent ?? DBNull.Value);
            ins.Parameters.AddWithValue("@model", (object?)row.Model ?? DBNull.Value);
            ins.Parameters.AddWithValue("@created", row.CreatedAt);
            ins.Parameters.AddWithValue("@createdMs", ms);
            ins.Parameters.AddWithValue("@payload", row.Payload);
            ins.ExecuteNonQuery();
        }

        using (var swap = conn.CreateCommand())
        {
            swap.Transaction = tx;
            swap.CommandText = """
                               DROP TABLE messages;
                               ALTER TABLE messages_new RENAME TO messages;
                               CREATE INDEX IF NOT EXISTS idx_messages_parent ON messages(parent_id);
                               """;
            swap.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
