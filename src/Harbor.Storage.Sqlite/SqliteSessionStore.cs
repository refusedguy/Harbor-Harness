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
public sealed class SqliteSessionStore : ISessionStore, ISessionCheckpointStore
{

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly object _initLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ILogger<SqliteSessionStore> _logger;
    private volatile bool _initialized;

    public SqliteSessionStore(string dbPath, ILogger<SqliteSessionStore> logger)
    {
        _dbPath = dbPath;
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
    }

    private ValueTask<SemaphoreSlim> GetSessionLockAsync(string sessionId, CancellationToken ct) =>
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
                cmd.Parameters.AddWithValue("@created", session.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@updated", session.UpdatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(session.Metadata, SqliteMappers.SessionMetadataInfo));
                cmd.Parameters.AddWithValue("@status", (int)session.Status);
                cmd.Parameters.AddWithValue("@kind", (int)session.Kind);
                cmd.Parameters.AddWithValue("@parent", (object?)session.ParentSessionId ?? DBNull.Value);
                cmd.ExecuteNonQuery();

                return session;
            }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to create session: {Error}", e));
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
                cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@createdMs", message.CreatedAt.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@payload", SqliteMappers.SerializeMessage(message));
                cmd.ExecuteNonQuery();

                // Update session.updated_at
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @sid";
                upd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                upd.Parameters.AddWithValue("@sid", sessionId);
                upd.ExecuteNonQuery();

                tx.Commit();
            }, ResultErrors.Message)
            .TapError(e => _logger.LogError("Failed to append message to session {SessionId}: {Error}", sessionId, e));
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
                cmd.Parameters.AddWithValue("@created", message.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@createdMs", message.CreatedAt.ToUnixTimeMilliseconds());
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

    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw). Absence (None) is
        // not a storage error — it keeps its own message shape, identical to
        // the other stores.
        var row = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (row.IsFailure)
            return row.ConvertFailure<IReadOnlyList<AgentMessage>>();
        if (row.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<IReadOnlyList<AgentMessage>>(SessionStoreErrors.SessionNotFound(sessionId));

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
                    result.Add(msg.Value); // guarded by the IsSuccess check.
                else
                    skipped.Add(msg.Error);
            }

            if (skipped.Count > 0)
                _logger.LogWarning("Skipped {Count} unreadable message(s) in session {SessionId}: {Errors}",
                    skipped.Count, sessionId, string.Join("; ", skipped));

            return (IReadOnlyList<AgentMessage>)result;
        }, ResultErrors.Message).ConfigureAwait(false);
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
    public async Task<Result<SessionCheckpoint>> CreateCheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();

        // #199: the expected "not found" outcomes are decided before the try
        // boundary so they stay plain failures (no throw, no error log).
        var sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
            return sessionRow.ConvertFailure<SessionCheckpoint>();
        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<SessionCheckpoint>(SessionStoreErrors.SessionNotFound(sessionId));

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                using var conn = OpenConnection();

                // The index is a count over the rows ordered up to and
                // including the anchor (created_at_ms ASC, rowid ASC — the
                // read path's exact ordering), so it cannot disagree with
                // the order it describes, and no history is materialized.
                // A missing anchor surfaces here as an empty scalar rather
                // than as a session-level failure — same decision point as
                // JsonlSessionStore's index scan.
                int index;
                using (var probe = conn.CreateCommand())
                {
                    probe.CommandText = """
                                        SELECT (SELECT COUNT(*) FROM messages x
                                                WHERE x.session_id = m.session_id
                                                  AND (x.created_at_ms < m.created_at_ms
                                                       OR (x.created_at_ms = m.created_at_ms AND x.rowid <= m.rowid))) - 1
                                        FROM messages m
                                        WHERE m.session_id = @sid AND m.id = @mid
                                        LIMIT 1
                                        """;
                    probe.Parameters.AddWithValue("@sid", sessionId);
                    probe.Parameters.AddWithValue("@mid", messageId);
                    object? scalar = probe.ExecuteScalar();
                    if (scalar is null or DBNull)
                        return Result.Failure<SessionCheckpoint>(
                            SessionStoreErrors.MessageNotFound(sessionId, messageId));
                    index = (int)(long)scalar;
                }

                var checkpoint = new SessionCheckpoint(
                    Guid.NewGuid().ToString("N"), sessionId, messageId, index,
                    DateTimeOffset.UtcNow, label);

                using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO checkpoints (id, session_id, message_id, message_index, created_at, label, rewind_of, removed)
                                     VALUES (@id, @sid, @mid, @idx, @created, @label, NULL, 0)
                                     """;
                insert.Parameters.AddWithValue("@id", checkpoint.Id);
                insert.Parameters.AddWithValue("@sid", sessionId);
                insert.Parameters.AddWithValue("@mid", messageId);
                insert.Parameters.AddWithValue("@idx", index);
                insert.Parameters.AddWithValue("@created", checkpoint.CreatedAt.ToString("O"));
                insert.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
                insert.ExecuteNonQuery();

                return Result.Success(checkpoint);
            }, ResultErrors.Message)
                .Bind(x => x)
                .TapError(e => _logger.LogError("Failed to record checkpoint in session {SessionId}: {Error}", sessionId, e));
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     List the session's checkpoints and rewind trail markers in
    ///     recording order (#1247). Recording order is rowid order — the
    ///     sqlite counterpart of the jsonl backend's file order.
    /// </summary>
    public async Task<Result<IReadOnlyList<SessionCheckpoint>>> ListCheckpointsAsync(
        string sessionId, CancellationToken ct = default)
    {
        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw, no error log).
        var sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
            return sessionRow.ConvertFailure<IReadOnlyList<SessionCheckpoint>>();
        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<IReadOnlyList<SessionCheckpoint>>(SessionStoreErrors.SessionNotFound(sessionId));

        return await Result.Try(async () =>
        {
            EnsureInitialized();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT id, session_id, message_id, message_index, created_at, label, rewind_of, removed
                              FROM checkpoints
                              WHERE session_id = @sid
                              ORDER BY rowid ASC
                              """;
            cmd.Parameters.AddWithValue("@sid", sessionId);

            var checkpoints = new List<SessionCheckpoint>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                checkpoints.Add(ReadCheckpoint(reader));
            return (IReadOnlyList<SessionCheckpoint>)checkpoints;
        }, ResultErrors.Message).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rewind the session to <paramref name="checkpointId" /> (#1247):
    ///     the history is truncated to the checkpoint's anchor message via
    ///     <see cref="DeleteMessagesAfterAsync" />, checkpoints anchored past
    ///     the anchor go with the dropped future, and a trail marker records
    ///     the rewind — the honest trail. The cleanup and the trail write
    ///     share one transaction, so a crash cannot leave a rewind recorded
    ///     without its dropped checkpoints gone (or the reverse).
    /// </summary>
    public async Task<Result<SessionRewindOutcome>> RewindToCheckpointAsync(
        string sessionId, string checkpointId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        // #199: the expected "not found" outcomes are decided before the try
        // boundary so they stay plain failures (no throw, no error log).
        var sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
            return sessionRow.ConvertFailure<SessionRewindOutcome>();
        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<SessionRewindOutcome>(SessionStoreErrors.SessionNotFound(sessionId));

        // Only plain checkpoints are restore points — a trail marker records
        // a past rewind, it is not itself a target (the jsonl backend draws
        // the same line with its !IsRewindTrail filter).
        var checkpointRow = await ReadCheckpointRowAsync(sessionId, checkpointId, ct).ConfigureAwait(false);
        if (checkpointRow.IsFailure)
            return checkpointRow.ConvertFailure<SessionRewindOutcome>();
        if (checkpointRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<SessionRewindOutcome>(SessionStoreErrors.CheckpointNotFound(sessionId, checkpointId));
#pragma warning disable CFE0001
        // CFE0001: false positive — the HasNoValue guard above is an early
        // return, a control-flow shape the analyzer does not model.
        SessionCheckpoint checkpoint = checkpointRow.Value;
#pragma warning restore CFE0001

        var truncated = await DeleteMessagesAfterAsync(sessionId, checkpoint.MessageId, ct).ConfigureAwait(false);
        if (truncated.IsFailure)
            return truncated.ConvertFailure<SessionRewindOutcome>();

#pragma warning disable CFE0001
        // CFE0001: false positive — the IsFailure guard above is an early return.
        int removed = truncated.Value;
#pragma warning restore CFE0001

        var trailId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

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

                // Checkpoints whose anchor message went with the dropped
                // future are dropped with them; checkpoints at or before
                // the anchor survive. The boundary is derived from "does
                // the anchor message still exist" rather than from the
                // recorded message_index, which an earlier rewind may have
                // left stale — the same survive/drop split the jsonl
                // backend draws by file order.
                cmd.CommandText = """
                                  DELETE FROM checkpoints
                                  WHERE session_id = @sid
                                    AND NOT EXISTS (
                                        SELECT 1 FROM messages m
                                        WHERE m.session_id = @sid AND m.id = checkpoints.message_id
                                    );
                                  INSERT INTO checkpoints (id, session_id, message_id, message_index, created_at, label, rewind_of, removed)
                                  VALUES (@id, @sid, @mid, @idx, @created, NULL, @rewindOf, @removed)
                                  """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@id", trailId);
                cmd.Parameters.AddWithValue("@mid", checkpoint.MessageId);
                cmd.Parameters.AddWithValue("@idx", checkpoint.MessageIndex);
                cmd.Parameters.AddWithValue("@created", now.ToString("O"));
                cmd.Parameters.AddWithValue("@rewindOf", checkpoint.Id);
                cmd.Parameters.AddWithValue("@removed", removed);
                cmd.ExecuteNonQuery();

                cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE session_id = @sid";
                long remaining = (long)cmd.ExecuteScalar();

                tx.Commit();
                return Result.Success(new SessionRewindOutcome(
                    checkpoint.Id, checkpoint.MessageId, removed, (int)remaining));
            }, ResultErrors.Message)
                .Bind(x => x)
                .TapError(e => _logger.LogError("Failed to record rewind in session {SessionId}: {Error}", sessionId, e));
        }
        finally
        {
            semaphore.Release();
        }
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
                    cmd.Parameters.AddWithValue("@status", (int)session.Status);
                    cmd.Parameters.AddWithValue("@kind", (int)session.Kind);
                    cmd.Parameters.AddWithValue("@parent", (object?)session.ParentSessionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
                    return cmd.ExecuteNonQuery();
                }, ResultErrors.Message)
                .TapError(e => _logger.LogError("Failed to update session {SessionId}: {Error}", session.Id, e))
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
    ///     Read one checkpoint row, plain checkpoints only
    ///     (<c>rewind_of IS NULL</c>). Absence (<see cref="Maybe{T}.None" />)
    ///     is not a storage error — the caller decides the failure text
    ///     (ROP-B П.24).
    /// </summary>
    private Task<Result<Maybe<SessionCheckpoint>>> ReadCheckpointRowAsync(
        string sessionId, string checkpointId, CancellationToken ct)
    {
        return Result.Try(async () =>
        {
            EnsureInitialized();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT id, session_id, message_id, message_index, created_at, label, rewind_of, removed
                              FROM checkpoints
                              WHERE session_id = @sid AND id = @cid AND rewind_of IS NULL
                              """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@cid", checkpointId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Maybe<SessionCheckpoint>.None;
            return Maybe.From(ReadCheckpoint(reader));
        }, ResultErrors.Message);
    }

    /// <summary>
    ///     Map one checkpoint row to the model. <c>created_at</c> is stored
    ///     round-trip ("O"); an unparseable stamp yields <see langword="default" />
    ///     instead of failing the whole list — the row-level tolerance of the
    ///     message read path, applied to checkpoint metadata.
    /// </summary>
    private static SessionCheckpoint ReadCheckpoint(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        DateTimeOffset.TryParse(reader.GetString(4), out var createdAt) ? createdAt : default,
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7));

    /// <summary>
    ///     One-time lazy initialization: create the directory, apply the schema,
    ///     migrate pre-#85 databases (global message PK, missing integer
    ///     ordering stamp) and pre-#1107 databases (missing status/kind/
    ///     parent_session_id columns). The constructor performs no I/O; every public
    ///     method funnels through here first, so init failures travel the
    ///     Result channel instead of throwing from the ctor.
    /// </summary>
    private void EnsureInitialized()
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
}
