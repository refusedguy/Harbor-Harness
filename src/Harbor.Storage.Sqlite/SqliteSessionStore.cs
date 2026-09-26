using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
namespace Harbor.Storage.Sqlite;
/// <summary>
///     SQLite-backed session storage.
///     Implements Repository pattern (GOF) via ISessionStore.
///     Use for: long-running deployments, many sessions, efficient queries.
///     Note: pulls in native e_sqlite3 (~1.5 MB) — use JsonlSessionStore if you want zero native deps.
/// </summary>
/// <remarks>
///     Construction is side-effect free: the database file and schema are
///     created lazily on first use (<see cref="EnsureInitialized" />), so
///     `new SqliteSessionStore(path, logger)` never touches the file system.
/// </remarks>
public sealed class SqliteSessionStore : ISessionStore
{

    private const string Schema = """
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
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new ContentPartJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly object _lock = new();
    private readonly ILogger<SqliteSessionStore> _logger;
    private volatile bool _initialized;

    public SqliteSessionStore(string dbPath, ILogger<SqliteSessionStore> logger)
    {
        _dbPath = dbPath;
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
    }

    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId,
        CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
        {
            EnsureInitialized();
            var session = Session.Create(directory, agentName, providerId, modelId);

            lock (_lock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  INSERT INTO sessions (id, project_id, directory, title, agent, model, provider_id, version, created_at, updated_at, metadata)
                                  VALUES (@id, @pid, @dir, @title, @agent, @model, @provider, @ver, @created, @updated, @meta)
                                  """;
                cmd.Parameters.AddWithValue("@id", session.Id);
                cmd.Parameters.AddWithValue("@pid", session.ProjectId);
                cmd.Parameters.AddWithValue("@dir", session.Directory);
                cmd.Parameters.AddWithValue("@title", session.Title);
                cmd.Parameters.AddWithValue("@agent", session.Agent);
                cmd.Parameters.AddWithValue("@model", session.Model);
                cmd.Parameters.AddWithValue("@provider", session.ProviderId);
                cmd.Parameters.AddWithValue("@ver", "0.2.0");
                cmd.Parameters.AddWithValue("@created", session.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@updated", session.UpdatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(session.Metadata, JsonOptions));
                cmd.ExecuteNonQuery();
            }

            return session;
        }, ResultErrors.Message))
        .TapError(e => _logger.LogError("Failed to create session: {Error}", e));
    }

    public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        return (await ReadRowAsync(sessionId, ct).ConfigureAwait(false))
            .Bind(row => row.ToResult($"Session '{sessionId}' not found."));
    }

    /// <summary>
    ///     Read one session row. Query failures travel the Result channel
    ///     (cancellation rethrown via <see cref="ResultErrors.Message" />);
    ///     a missing row is absence (<see cref="Maybe{T}.None" />), not an
    ///     error — "not found" stays distinguishable from a storage failure
    ///     instead of sharing the same Error channel (ROP-B П.24).
    /// </summary>
    private Task<Result<Maybe<Session>>> ReadRowAsync(string sessionId, CancellationToken ct)
    {
        return Result.Try(async () =>
        {
            EnsureInitialized();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM sessions WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", sessionId);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Maybe<Session>.None;

            return Maybe.From(ReadSession(reader));
        }, ResultErrors.Message);
    }

    public async Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        return await Result.Try(async () =>
        {
            EnsureInitialized();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();

            if (string.IsNullOrEmpty(projectId))
            {
                cmd.CommandText = "SELECT * FROM sessions ORDER BY updated_at DESC";
            }
            else
            {
                cmd.CommandText = "SELECT * FROM sessions WHERE project_id = @pid ORDER BY updated_at DESC";
                cmd.Parameters.AddWithValue("@pid", projectId);
            }

            var result = new List<Session>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(ReadSession(reader));
            }

            return (IReadOnlyList<Session>)result;
        }, ResultErrors.Message).ConfigureAwait(false);
    }

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
        {
            EnsureInitialized();
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO messages (id, session_id, parent_id, role, agent, model, created_at, created_at_ms, payload)
                                  VALUES (@id, @sid, @pid, @role, @agent, @model, @created, @createdMs, @payload)
                                  """;
                cmd.Parameters.AddWithValue("@id", message.Id);
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@pid", (object?)message.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@role", message.Role);
                cmd.Parameters.AddWithValue("@agent", message is UserMessage u ? u.Agent : DBNull.Value);
                cmd.Parameters.AddWithValue("@model", message is UserMessage um ? um.Model : message is AssistantMessage a ? a.Model : DBNull.Value);
                cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@createdMs", message.CreatedAt.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(message, message.GetType(), JsonOptions));
                cmd.ExecuteNonQuery();

                // Update session.updated_at
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @sid";
                upd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                upd.Parameters.AddWithValue("@sid", sessionId);
                upd.ExecuteNonQuery();

                tx.Commit();
            }
        }, ResultErrors.Message))
        .TapError(e => _logger.LogError("Failed to append message to session {SessionId}: {Error}", sessionId, e));
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // For SQLite we replace by (session_id, id)
        return Task.FromResult(Result.Try(() =>
        {
            EnsureInitialized();
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  UPDATE messages SET payload = @payload, role = @role, created_at = @created, created_at_ms = @createdMs
                                  WHERE id = @id AND session_id = @sid
                                  """;
                cmd.Parameters.AddWithValue("@id", message.Id);
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@role", message.Role);
                cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@createdMs", message.CreatedAt.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(message, message.GetType(), JsonOptions));
                cmd.ExecuteNonQuery();
            }
        }, ResultErrors.Message));
    }

    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        return await Result.Try(async () =>
        {
            EnsureInitialized();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            // Chronological by instant (Unix-ms stamp, same semantics as
            // JsonlSessionStore's OrderBy(CreatedAt)); rowid breaks ties.
            // created_at TEXT is display-only — ISO-8601 text does NOT sort
            // chronologically across mixed UTC offsets.
            cmd.CommandText = "SELECT role, payload FROM messages WHERE session_id = @sid ORDER BY created_at_ms ASC, rowid ASC";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            var result = new List<AgentMessage>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string role = reader.GetString(0);
                string payload = reader.GetString(1);
                // AgentMessage is abstract and has no [JsonDerivedType] discriminator,
                // so we have to pick the concrete type from the role column ourselves.
                var msg = DeserializeMessage(role, payload);
                if (msg is not null) result.Add(msg);
            }

            return (IReadOnlyList<AgentMessage>)result;
        }, ResultErrors.Message).ConfigureAwait(false);
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
        {
            EnsureInitialized();
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM sessions WHERE id = @id; DELETE FROM messages WHERE session_id = @id;";
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.ExecuteNonQuery();
            }
        }, ResultErrors.Message));
    }

    /// <summary>
    ///     "Rewind to here": delete every message ordered after the target row.
    ///     Ordering follows the same created_at_ms ASC used by
    ///     <see cref="GetMessagesAsync" /> (Unix-ms instant, matching
    ///     JsonlSessionStore's DateTimeOffset ordering),
    ///     with rowid as the deterministic tie-breaker for equal timestamps.
    /// </summary>
    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
        {
            EnsureInitialized();
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                int deleted;
                using (var scope = conn.BeginTransaction())
                {
                    // Anchor: created_at_ms of the kept message; ties broken by its rowid.
                    cmd.Transaction = scope;
                    cmd.CommandText = """
                        SELECT created_at_ms, rowid FROM messages
                        WHERE session_id = @sid AND id = @mid LIMIT 1
                        """;
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@mid", messageId);

                    long anchorMs;
                    long anchorRowId;
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            throw new InvalidOperationException(
                                $"Message '{messageId}' not found in session '{sessionId}'.");
                        }

                        anchorMs = reader.GetInt64(0);
                        anchorRowId = reader.GetInt64(1);
                    }

                    cmd.CommandText = """
                        DELETE FROM messages
                        WHERE session_id = @sid
                          AND (created_at_ms > @anchor OR (created_at_ms = @anchor AND rowid > @rid))
                        """;
                    cmd.Parameters.AddWithValue("@anchor", anchorMs);
                    cmd.Parameters.AddWithValue("@rid", anchorRowId);
                    deleted = cmd.ExecuteNonQuery();

                    using var upd = conn.CreateCommand();
                    upd.Transaction = scope;
                    upd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @sid";
                    upd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                    upd.Parameters.AddWithValue("@sid", sessionId);
                    upd.ExecuteNonQuery();

                    scope.Commit();
                }

                return deleted;
            }
        }, ResultErrors.Message));
    }

    public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        return await Result.Try(async () =>
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT metadata FROM sessions WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", sessionId);

                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }, ResultErrors.Message)
            .Bind(meta => meta is null or DBNull
                ? Result.Failure<SessionMetadata>($"Session '{sessionId}' not found.")
                : Result.Success(
                    JsonSerializer.Deserialize<SessionMetadata>((string)meta, JsonOptions)
                    ?? SessionMetadata.Empty))
            .ConfigureAwait(false);
    }

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
        {
            EnsureInitialized();
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE sessions SET metadata = @meta WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(metadata, JsonOptions));
                cmd.ExecuteNonQuery();
            }
        }, ResultErrors.Message));
    }

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
            {
                EnsureInitialized();
                lock (_lock)
                {
                    using var conn = OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                                      UPDATE sessions SET 
                                          title = @title, 
                                          updated_at = @updated 
                                      WHERE id = @id
                                      """;
                    cmd.Parameters.AddWithValue("@id", session.Id);
                    cmd.Parameters.AddWithValue("@title", session.Title);
                    cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
                    return cmd.ExecuteNonQuery();
                }
            }, ResultErrors.Message))
            .TapError(e => _logger.LogError("Failed to update session {SessionId}: {Error}", session.Id, e))
            .Bind(rows => rows == 0
                ? Result.Failure($"Session '{session.Id}' not found.")
                : Result.Success());
    }

    /// <summary>
    ///     One-time lazy initialization: create the directory, apply the schema,
    ///     and migrate pre-#85 databases (global message PK, missing integer
    ///     ordering stamp). The constructor performs no I/O; every public
    ///     method funnels through here first, so init failures travel the
    ///     Result channel instead of throwing from the ctor.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();

            MigrateMessagesIfNeeded(conn);

            _initialized = true;
        }
    }

    /// <summary>
    ///     Migrate databases created before #85:
    ///     (1) global PRIMARY KEY (id) → composite (session_id, id);
    ///     (2) missing created_at_ms integer stamp → add + backfill from text;
    ///     (3) ordering index rebuilt over the integer stamp.
    /// </summary>
    private static void MigrateMessagesIfNeeded(SqliteConnection conn)
    {
        if (HasLegacyMessagePk(conn))
            RebuildMessagesTable(conn);

        if (!HasColumn(conn, "messages", "created_at_ms"))
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

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
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

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Recommended PRAGMAs for performance and concurrency
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                                 PRAGMA journal_mode = WAL;
                                 PRAGMA synchronous = NORMAL;
                                 PRAGMA busy_timeout = 5000;
                                 PRAGMA cache_size = -8000;  -- 8 MB (default 2 MB)
                                 PRAGMA foreign_keys = ON;
                                 """;
            pragma.ExecuteNonQuery();
        }

        return conn;
    }

    private static AgentMessage? DeserializeMessage(string role, string payload)
    {
        return role switch
        {
            "user" => JsonSerializer.Deserialize<UserMessage>(payload, JsonOptions),
            "assistant" => JsonSerializer.Deserialize<AssistantMessage>(payload, JsonOptions),
            "tool_result" => JsonSerializer.Deserialize<ToolResultMessage>(payload, JsonOptions),
            _ => null
        };
    }

    private static Session ReadSession(DbDataReader reader)
    {
        string id = reader.GetString(reader.GetOrdinal("id"));
        string projectId = reader.GetString(reader.GetOrdinal("project_id"));
        string directory = reader.GetString(reader.GetOrdinal("directory"));
        string title = reader.GetString(reader.GetOrdinal("title"));
        string agent = reader.GetString(reader.GetOrdinal("agent"));
        string model = reader.GetString(reader.GetOrdinal("model"));
        string providerId = reader.GetString(reader.GetOrdinal("provider_id"));
        var createdAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")));
        string metaJson = reader.GetString(reader.GetOrdinal("metadata"));
        var meta = JsonSerializer.Deserialize<SessionMetadata>(metaJson, JsonOptions) ?? SessionMetadata.Empty;

        return new Session(
            id,
            projectId,
            directory,
            title,
            agent,
            model,
            providerId,
            createdAt,
            updatedAt,
            meta);
    }

    private sealed class ContentPartJsonConverter : JsonConverter<ContentPart>
    {
        public override ContentPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var el = doc.RootElement;
            string? type = el.TryGetProperty("type", out var tp) ? tp.GetString() : el.TryGetProperty("Type", out var tp2) ? tp2.GetString() : null;
            return type switch
            {
                "text" => new TextPart(el.TryGetProperty("text", out var t) ? t.GetString()! : el.GetProperty("Text").GetString()!),
                "thinking" => new ThinkingPart(el.TryGetProperty("text", out var t) ? t.GetString()! : el.GetProperty("Text").GetString()!),
                "tool_call" => new ToolCallPart(
                    el.TryGetProperty("id", out var id) ? id.GetString()! : el.GetProperty("Id").GetString()!,
                    el.TryGetProperty("toolName", out var tn) ? tn.GetString()! : el.GetProperty("ToolName").GetString()!,
                    el.TryGetProperty("args", out var a) ? a.Clone() : el.TryGetProperty("Args", out var a2) ? a2.Clone() : default),
                "file" => new FilePart(
                    el.TryGetProperty("path", out var p) ? p.GetString()! : el.GetProperty("Path").GetString()!,
                    el.TryGetProperty("mimeType", out var mt) ? mt.GetString()! : el.GetProperty("MimeType").GetString()!,
                    el.TryGetProperty("sizeBytes", out var sb) ? sb.GetInt64() : el.GetProperty("SizeBytes").GetInt64()),
                _ => null
            };
        }

        public override void Write(Utf8JsonWriter writer, ContentPart value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case TextPart t:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", t.Text);
                    break;
                case ThinkingPart th:
                    writer.WriteString("type", "thinking");
                    writer.WriteString("text", th.Text);
                    break;
                case ToolCallPart tc:
                    writer.WriteString("type", "tool_call");
                    writer.WriteString("id", tc.Id);
                    writer.WriteString("toolName", tc.ToolName);
                    writer.WritePropertyName("args");
                    JsonSerializer.Serialize(writer, tc.Args, options);
                    break;
                case FilePart f:
                    writer.WriteString("type", "file");
                    writer.WriteString("path", f.Path);
                    writer.WriteString("mimeType", f.MimeType);
                    writer.WriteNumber("sizeBytes", f.SizeBytes);
                    break;
            }
            writer.WriteEndObject();
        }
    }
}
