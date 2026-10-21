using System.Collections.Concurrent;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
namespace Harbor.Storage.Sqlite;
/// <summary>
///     SQLite-backed session storage.
///     Implements Repository pattern (GOF) via ISessionStore, plus the
///     #1247 checkpoint seam (ISessionCheckpointStore).
///     Use for: long-running deployments, many sessions, efficient queries.
///     Note: pulls in native e_sqlite3 (~1.5 MB) — use JsonlSessionStore if you want zero native deps.
/// </summary>
/// <remarks>
///     Construction is side-effect free: the database file and schema are
///     created lazily on first use (<see cref="EnsureInitialized" />), so
///     `new SqliteSessionStore(path, logger)` never touches the file system.
///     <para>
///         <b>Locking (#184):</b> per-session <see cref="SemaphoreSlim" /> strip
///         (shared implementation: <c>Harbor.Storage.Shared.SessionLockStrip</c>)
///         instead of the former global <c>lock (_lock)</c> — operations on
///         different sessions proceed concurrently, same-session mutations
///         stay serialized. A dedicated <c>_initLock</c> guards only the
///         one-time schema initialization. Pure reads never took the lock and
///         still don't (WAL + busy_timeout carry them).
///     </para>
/// </remarks>
public sealed partial class SqliteSessionStore : ISessionStore, ISessionCheckpointStore, ICompactionEventStore
{

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly object _initLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ILogger<SqliteSessionStore> _logger;
    private volatile bool _initialized;

    // #1831 (#95 slice): the checkpoint/rewind axis lives in
    // SqliteCheckpointStore (verbatim move); this store keeps thin forwarders
    // so the ISessionCheckpointStore impl site stays where it is.
    private readonly SqliteCheckpointStore _checkpoints;

    public SqliteSessionStore(string dbPath, ILogger<SqliteSessionStore> logger)
    {
        _dbPath = dbPath;
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        _checkpoints = new SqliteCheckpointStore(this, logger);
    }

    /// <summary>
    ///     Per-session mutex. <b>#1831:</b> shared with the checkpoint axis —
    ///     it is the same semaphore, so a checkpoint write still serializes
    ///     against a message append on the same session.
    /// </summary>
    internal ValueTask<SemaphoreSlim> GetSessionLockAsync(string sessionId, CancellationToken ct) =>
        // Same strip primitive as JsonlSessionStore (#184).
        SessionLockStrip.AcquireAsync(_sessionLocks, sessionId, ct);

    public async Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        var session = Session.Create(directory, agentName, providerId, modelId);

        var semaphore = await GetSessionLockAsync(session.Id, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  INSERT INTO sessions (id, project_id, directory, title, agent, model, provider_id, version, created_at, updated_at, metadata, status, kind, parent_session_id)
                                  VALUES (@id, @pid, @dir, @title, @agent, @model, @provider, @ver, @created, @updated, @meta, @status, @kind, @parent)
                                  """;
                cmd.Parameters.AddWithValue("@id", session.Id);
                cmd.Parameters.AddWithValue("@pid", session.ProjectId);
                cmd.Parameters.AddWithValue("@dir", session.Directory);
                cmd.Parameters.AddWithValue("@title", session.Title);
                cmd.Parameters.AddWithValue("@agent", session.Agent);
                cmd.Parameters.AddWithValue("@model", session.Model);
                cmd.Parameters.AddWithValue("@provider", session.ProviderId);
                cmd.Parameters.AddWithValue("@ver", "0.2.0");
                cmd.Parameters.Add(StampParam("@created", session.CreatedAt));
                cmd.Parameters.Add(StampParam("@updated", session.UpdatedAt));
                cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(session.Metadata, SqliteMappers.SessionMetadataInfo));
                cmd.Parameters.Add(IntParam("@status", (int)session.Status));
                cmd.Parameters.Add(IntParam("@kind", (int)session.Kind));
                cmd.Parameters.AddWithValue("@parent", (object?)session.ParentSessionId ?? DBNull.Value);
                cmd.ExecuteNonQuery();

                return session;
            }, ResultErrors.Message)
            .TapError(e => SqliteSessionStoreLog.CreateSessionFailed(_logger, e));
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        return (await ReadRowAsync(sessionId, ct).ConfigureAwait(false))
            .Bind(row => row.ToResult(SessionStoreErrors.SessionNotFound(sessionId)));
    }

    /// <summary>
    ///     Read one session row. Query failures travel the Result channel
    ///     (cancellation rethrown via <see cref="ResultErrors.Message" />);
    ///     a missing row is absence (<see cref="Maybe{T}.None" />), not an
    ///     error — "not found" stays distinguishable from a storage failure
    ///     instead of sharing the same Error channel (ROP-B П.24).
    ///     <b>#1831:</b> shared with the checkpoint axis — it is the same read,
    ///     so the "session exists" policy is decided in one place.
    /// </summary>
    internal Task<Result<Maybe<Session>>> ReadRowAsync(string sessionId, CancellationToken ct)
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

            return Maybe.From(SqliteMappers.ReadSession(reader));
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
                result.Add(SqliteMappers.ReadSession(reader));
            }

            return (IReadOnlyList<Session>)result;
        }, ResultErrors.Message).ConfigureAwait(false);
    }

    public async Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        EnsureInitialized();

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
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
                cmd.Parameters.Add(StampParam("@created", message.CreatedAt));
                cmd.Parameters.Add(LongParam("@createdMs", message.CreatedAt.ToUnixTimeMilliseconds()));
                cmd.Parameters.AddWithValue("@payload", SqliteMappers.SerializeMessage(message));
                cmd.ExecuteNonQuery();

                // Update session.updated_at
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @sid";
                upd.Parameters.Add(StampParam("@now", DateTimeOffset.UtcNow));
                upd.Parameters.AddWithValue("@sid", sessionId);
                upd.ExecuteNonQuery();

                tx.Commit();
            }, ResultErrors.Message)
            .TapError(e => SqliteSessionStoreLog.AppendMessageFailed(_logger, sessionId, e));
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        // For SQLite we replace by id. A 0-row update means the session or the
        // message does not exist — an honest Failure, not a silent no-op
        // (same rows==0 pattern as UpdateAsync).
        EnsureInitialized();

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
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
                cmd.Parameters.Add(StampParam("@created", message.CreatedAt));
                cmd.Parameters.Add(LongParam("@createdMs", message.CreatedAt.ToUnixTimeMilliseconds()));
                cmd.Parameters.AddWithValue("@payload", SqliteMappers.SerializeMessage(message));
                return cmd.ExecuteNonQuery();
            }, ResultErrors.Message)
            .Bind(rows => rows == 0
                ? Result.Failure(SessionStoreErrors.MessageNotFound(sessionId, message.Id))
                : Result.Success());
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Read all messages for a session in chronological order.
    /// </summary>
    /// <remarks>
    ///     <b>#1744:</b> this returns the LOG's rows — what the database holds,
    ///     unaltered. The reader's view of the same read (the transcript with
    ///     the interrupted turns closed in memory, and what the read could not
    ///     deliver) is <see cref="GetTranscriptAsync" />.
    /// </remarks>
    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        var read = await ReadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (read.IsFailure)
            return read.ConvertFailure<IReadOnlyList<AgentMessage>>();

        // (read.Value is guarded by the failure check above.)
        var messages = read.Value.Messages;
        return Result.Success(messages);
    }

    /// <summary>
    ///     Read the session's transcript: the messages a reader gets — with the
    ///     turns the log never closed closed in memory — together with what the
    ///     read could not deliver (#1744).
    /// </summary>
    /// <remarks>
    ///     A row is written in one transaction, so a row can never be half
    ///     written the way a JSONL record can. What CAN happen is a row that
    ///     does not decode (a corrupt payload): it is skipped and counted, and
    ///     when it is the newest row the tail of the session is missing.
    /// </remarks>
    public async Task<Result<SessionTranscript>> GetTranscriptAsync(string sessionId, CancellationToken ct = default)
    {
        var read = await ReadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (read.IsFailure)
            return read.ConvertFailure<SessionTranscript>();

        // (read.Value is guarded by the failure check above.)
        var transcript = read.Value.Transcript;
        return Result.Success(transcript);
    }

    /// <summary>
    ///     ONE read of a session: the log's rows and the reader's view of the
    ///     same read, from the same pass over the table (#1744).
    /// </summary>
    private async Task<Result<SqliteSessionRead>> ReadSessionAsync(string sessionId, CancellationToken ct)
    {
        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw). Absence (None) is
        // not a storage error — it keeps its own message shape, identical to
        // the other stores.
        var row = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (row.IsFailure)
            return row.ConvertFailure<SqliteSessionRead>();
        if (row.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<SqliteSessionRead>(SessionStoreErrors.SessionNotFound(sessionId));

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
            var skipped = new List<string>();
            // #1744: whether the NEWEST row is one of the unreadable ones —
            // the rows arrive in order, so the last one read is the tail.
            bool tailUnreadable = false;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string role = reader.GetString(0);
                string payload = reader.GetString(1);
                // #199: one unreadable row must not fail the whole history —
                // same warn-and-skip semantics as JsonlSessionStore's
                // per-line parse (see ParseMessagesFromDiskAsync).
                var msg = SqliteMappers.TryDeserializeMessage(role, payload);
                if (msg.IsSuccess)
                {
                    result.Add(msg.Value); // guarded by the IsSuccess check.
                    tailUnreadable = false;
                }
                else
                {
                    skipped.Add(msg.Error);
                    tailUnreadable = true;
                }
            }

            if (skipped.Count > 0)
                SqliteSessionStoreLog.SkippedUnreadableMessages(_logger, skipped.Count, sessionId, string.Join("; ", skipped));

            // The reader's view: the turns the log never closed closed in
            // memory, plus what the read could not deliver.
            var unclosed = TranscriptInspector.FindUnclosedToolCalls(result);
            var integrity = new TranscriptIntegrity(skipped.Count, tailUnreadable, unclosed);
            var balanced = TranscriptInspector.CloseUnclosedToolCalls(result);

            return Result.Success(new SqliteSessionRead(result, new SessionTranscript(balanced, integrity)));
        }, ResultErrors.Message)
            .Bind(x => x)
            .ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        EnsureInitialized();

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM sessions WHERE id = @id;";
                cmd.Parameters.AddWithValue("@id", sessionId);
                int rows = cmd.ExecuteNonQuery();
                using var msgCmd = conn.CreateCommand();
                msgCmd.CommandText = "DELETE FROM messages WHERE session_id = @id;";
                msgCmd.Parameters.AddWithValue("@id", sessionId);
                msgCmd.ExecuteNonQuery();
                return rows;
            }, ResultErrors.Message)
            .Bind(rows => rows == 0
                ? Result.Failure(SessionStoreErrors.SessionNotFound(sessionId))
                : Result.Success())
            .Tap(() => SessionLockStrip.Evict(_sessionLocks, sessionId));
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     "Rewind to here": delete every message ordered after the target row.
    ///     Ordering follows the same created_at_ms ASC used by
    ///     <see cref="GetMessagesAsync" /> (Unix-ms instant, matching
    ///     JsonlSessionStore's DateTimeOffset ordering),
    ///     with rowid as the deterministic tie-breaker for equal timestamps.
    /// </summary>
    public async Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        // #199: the whole body (including the expected "message not found"
        // outcome) rides one Result rail — the anchor miss returns a failure
        // carrying the session + message ids instead of throwing, and the
        // outer Bind flattens the nested Result.
        EnsureInitialized();

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
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

                    bool found;
                    long anchorMs = 0;
                    long anchorRowId = 0;
                    using (var reader = cmd.ExecuteReader())
                    {
                        found = reader.Read();
                        if (found)
                        {
                            anchorMs = reader.GetInt64(0);
                            anchorRowId = reader.GetInt64(1);
                        }
                    }

                    if (!found)
                        return Result.Failure<int>(
                            SessionStoreErrors.MessageNotFound(sessionId, messageId));

                    cmd.CommandText = """
                        DELETE FROM messages
                        WHERE session_id = @sid
                          AND (created_at_ms > @anchor OR (created_at_ms = @anchor AND rowid > @rid))
                        """;
                    cmd.Parameters.Add(LongParam("@anchor", anchorMs));
                    cmd.Parameters.Add(LongParam("@rid", anchorRowId));
                    deleted = cmd.ExecuteNonQuery();

                    using var upd = conn.CreateCommand();
                    upd.Transaction = scope;
                    upd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @sid";
                    upd.Parameters.Add(StampParam("@now", DateTimeOffset.UtcNow));
                    upd.Parameters.AddWithValue("@sid", sessionId);
                    upd.ExecuteNonQuery();

                    scope.Commit();
                }

                return Result.Success(deleted);
            }, ResultErrors.Message).Bind(x => x);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Mark a checkpoint over <paramref name="messageId" /> (#1247): the
    ///     anchor's zero-based index is derived by the same ordering
    ///     <see cref="GetMessagesAsync" /> reads with, and the row is
    ///     inserted under the per-session lock.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="SqliteCheckpointStore" /> (#1831, #95
    ///     slice: verbatim move) — the checkpoint axis has one home; the
    ///     <see cref="ISessionCheckpointStore" /> impl site stays here.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Anchor message id to mark.</param>
    /// <param name="label">Optional human label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded checkpoint, or failure if session/message not found.</returns>
    public Task<Result<SessionCheckpoint>> CreateCheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default) =>
        _checkpoints.CreateCheckpointAsync(sessionId, messageId, label, ct);

    /// <summary>
    ///     List the session's checkpoints and rewind trail markers in
    ///     recording order (#1247). Recording order is rowid order — the
    ///     sqlite counterpart of the jsonl backend's file order.
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="SqliteCheckpointStore" /> (#1831, #95
    ///     slice: verbatim move).
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The checkpoints, or failure if the session does not exist.</returns>
    public Task<Result<IReadOnlyList<SessionCheckpoint>>> ListCheckpointsAsync(
        string sessionId, CancellationToken ct = default) =>
        _checkpoints.ListCheckpointsAsync(sessionId, ct);

    /// <summary>
    ///     Rewind the session to <paramref name="checkpointId" /> (#1247):
    ///     the history is truncated to the checkpoint's anchor message via
    ///     <see cref="DeleteMessagesAfterAsync" />, checkpoints anchored past
    ///     the anchor go with the dropped future, and a trail marker records
    ///     the rewind — the honest trail. The cleanup and the trail write
    ///     share one transaction, so a crash cannot leave a rewind recorded
    ///     without its dropped checkpoints gone (or the reverse).
    /// </summary>
    /// <remarks>
    ///     Forwarder over <see cref="SqliteCheckpointStore" /> (#1831, #95
    ///     slice: verbatim move).
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="checkpointId">Id of the checkpoint to restore (from <see cref="ListCheckpointsAsync" />).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were dropped and how many remain.</returns>
    public Task<Result<SessionRewindOutcome>> RewindToCheckpointAsync(
        string sessionId, string checkpointId, CancellationToken ct = default) =>
        _checkpoints.RewindToCheckpointAsync(sessionId, checkpointId, ct);

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
                ? Result.Failure<SessionMetadata>(SessionStoreErrors.SessionNotFound(sessionId))
                : SqliteMappers.TryDeserializeMetadata((string)meta, sessionId))
            .ConfigureAwait(false);
    }

    public async Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        EnsureInitialized();

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE sessions SET metadata = @meta WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(metadata, SqliteMappers.SessionMetadataInfo));
                return cmd.ExecuteNonQuery();
            }, ResultErrors.Message)
            .Bind(rows => rows == 0
                ? Result.Failure(SessionStoreErrors.SessionNotFound(sessionId))
                : Result.Success());
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
    {
        EnsureInitialized();

        var semaphore = await GetSessionLockAsync(session.Id, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
                {
                    using var conn = OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                                      UPDATE sessions SET 
                                          title = @title, 
                                          status = @status,
                                          kind = @kind,
                                          parent_session_id = @parent,
                                          updated_at = @updated 
                                      WHERE id = @id
                                      """;
                    cmd.Parameters.AddWithValue("@id", session.Id);
                    cmd.Parameters.AddWithValue("@title", session.Title);
                    cmd.Parameters.Add(IntParam("@status", (int)session.Status));
                    cmd.Parameters.Add(IntParam("@kind", (int)session.Kind));
                    cmd.Parameters.AddWithValue("@parent", (object?)session.ParentSessionId ?? DBNull.Value);
                    cmd.Parameters.Add(StampParam("@updated", DateTimeOffset.UtcNow));
                    return cmd.ExecuteNonQuery();
                }, ResultErrors.Message)
                .TapError(e => SqliteSessionStoreLog.UpdateSessionFailed(_logger, session.Id, e))
                .Bind(rows => rows == 0
                    ? Result.Failure(SessionStoreErrors.SessionNotFound(session.Id))
                    : Result.Success());
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     One-time lazy initialization: create the directory, apply the schema,
    ///     migrate pre-#85 databases (global message PK, missing integer
    ///     ordering stamp) and pre-#1107 databases (missing status/kind/
    ///     parent_session_id columns). The constructor performs no I/O; every public
    ///     method funnels through here first, so init failures travel the
    ///     Result channel instead of throwing from the ctor.
    ///     <b>#1831:</b> shared with the checkpoint axis — it is the same
    ///     one-time initialization, so the schema is applied once for both.
    /// </summary>
    internal void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SqliteSchema.Schema;
            cmd.ExecuteNonQuery();

            SqliteSchema.MigrateSessionsIfNeeded(conn);
            SqliteSchema.MigrateMessagesIfNeeded(conn);

            _initialized = true;
        }
    }

    // ── #1534: typed-parameter shaping for the append/write hot paths ─────

    /// <summary>
    ///     THE single cached round-trip ("O") timestamp path (issue #1534).
    ///     Every TEXT date column — sessions.created_at/updated_at,
    ///     messages.created_at, checkpoints.created_at — formats through this
    ///     one method, and every read (<c>SqliteMappers.ReadSession</c>,
    ///     <c>SqliteCheckpointStore.ReadCheckpoint</c>) parses the same text back with
    ///     <c>DateTimeOffset.Parse</c>. "O" is the round-trip specifier, so the
    ///     parse returns the original instant with its offset intact and the
    ///     symmetry is pinned by <c>SqliteIsoStampRoundTripTests</c>. The
    ///     chronology authority on message rows is the INTEGER
    ///     <c>created_at_ms</c> column (ticks, not text); the TEXT stamp stays
    ///     display-only and keeps <c>ListAsync</c>'s lexicographic
    ///     <c>ORDER BY updated_at</c> semantics exactly as they were.
    /// </summary>
    private static string Timestamp(DateTimeOffset value) => value.ToString("O");

    /// <summary>
    ///     Typed parameter builders for the write paths (issue #1534). The
    ///     column's <see cref="SqliteType" /> is known statically at every
    ///     callsite, so each parameter carries an explicit one instead of
    ///     <c>SqliteParameterCollection.AddWithValue</c>'s per-call
    ///     type inference from the runtime value — what #97's "N× boxed
    ///     AddWithValue" line described. The <see cref="SqliteParameter.Value" />
    ///     setter itself still boxes the value once (a binder-API shape, not
    ///     an inference cost); what these builders remove is the per-call
    ///     inference walk plus the per-site duplicated "O" formatting.
    ///     <b>#1831:</b> shared with the checkpoint axis — the axis writes
    ///     checkpoints.created_at and checkpoints.message_index through the
    ///     same builders, so both halves format a column the same way.
    /// </summary>
    internal static SqliteParameter IntParam(string name, int value) =>
        new(name, SqliteType.Integer) { Value = value };

    private static SqliteParameter LongParam(string name, long value) =>
        new(name, SqliteType.Integer) { Value = value };

    internal static SqliteParameter StampParam(string name, DateTimeOffset value) =>
        new(name, SqliteType.Text) { Value = Timestamp(value) };

    /// <summary>
    ///     Open a connection to the database file. <b>#1831:</b> shared with
    ///     the checkpoint axis — it is the same connection string and the
    ///     same PRAGMAs, so the axis never opens a connection of its own.
    /// </summary>
    internal SqliteConnection OpenConnection()
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
}

/// <summary>
///     One read of a session: the log's rows and the reader's view of the same
///     read (#1744). Both come from ONE pass over the table — a second read
///     would be a second snapshot, and the two halves could describe different
///     moments.
/// </summary>
/// <param name="Messages">The log's messages, in chronological order.</param>
/// <param name="Transcript">The reader's view: the closed transcript and its integrity.</param>
internal sealed record SqliteSessionRead(
    IReadOnlyList<AgentMessage> Messages,
    SessionTranscript Transcript);

/// <summary>
///     SG8: BCL <c>[LoggerMessage]</c> delegates for <see cref="SqliteSessionStore" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SqliteSessionStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to create session: {Error}")]
    public static partial void CreateSessionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to append message to session {SessionId}: {Error}")]
    public static partial void AppendMessageFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Skipped {Count} unreadable message(s) in session {SessionId}: {Errors}")]
    public static partial void SkippedUnreadableMessages(ILogger logger, int count, string sessionId, string errors);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to record checkpoint in session {SessionId}: {Error}")]
    public static partial void RecordCheckpointFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to record rewind in session {SessionId}: {Error}")]
    public static partial void RecordRewindFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to update session {SessionId}: {Error}")]
    public static partial void UpdateSessionFailed(ILogger logger, string sessionId, string error);
}
