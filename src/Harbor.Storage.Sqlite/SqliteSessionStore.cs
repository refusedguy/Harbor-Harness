using System.Data.Common;
using System.Text.Json;
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
                                      id TEXT PRIMARY KEY,
                                      session_id TEXT NOT NULL,
                                      parent_id TEXT,
                                      role TEXT NOT NULL,
                                      agent TEXT,
                                      model TEXT,
                                      created_at TEXT NOT NULL,
                                      payload TEXT NOT NULL,
                                      FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
                                  );
                                  CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id, created_at);
                                  CREATE INDEX IF NOT EXISTS idx_messages_parent ON messages(parent_id);
                                  """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly object _lock = new();
    private readonly ILogger<SqliteSessionStore> _logger;
    private bool _initialized;

    public SqliteSessionStore(string dbPath, ILogger<SqliteSessionStore> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        Initialize();
    }

    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId,
        CancellationToken ct = default)
    {
        return Task.FromResult(Result.Try(() =>
        {
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
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO messages (id, session_id, parent_id, role, agent, model, created_at, payload)
                                  VALUES (@id, @sid, @pid, @role, @agent, @model, @created, @payload)
                                  """;
                cmd.Parameters.AddWithValue("@id", message.Id);
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@pid", (object?)message.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@role", message.Role);
                cmd.Parameters.AddWithValue("@agent", message is UserMessage u ? u.Agent : DBNull.Value);
                cmd.Parameters.AddWithValue("@model", message is UserMessage um ? um.Model : message is AssistantMessage a ? a.Model : DBNull.Value);
                cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToString("O"));
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
        // For SQLite we replace by id
        return Task.FromResult(Result.Try(() =>
        {
            lock (_lock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  UPDATE messages SET payload = @payload, role = @role, created_at = @created
                                  WHERE id = @id AND session_id = @sid
                                  """;
                cmd.Parameters.AddWithValue("@id", message.Id);
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@role", message.Role);
                cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(message, message.GetType(), JsonOptions));
                cmd.ExecuteNonQuery();
            }
        }, ResultErrors.Message));
    }

    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        return await Result.Try(async () =>
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT role, payload FROM messages WHERE session_id = @sid ORDER BY created_at ASC";
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

    public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        return await Result.Try(async () =>
            {
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

    private void Initialize()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();

            _initialized = true;
        }
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
}
