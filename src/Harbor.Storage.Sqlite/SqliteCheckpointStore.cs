// SqliteCheckpointStore.cs — #1831 (#95 slice): the checkpoint/rewind axis of
// the SQLite store, moved verbatim out of SqliteSessionStore.cs.
//
// The store keeps thin forwarders, so the ISessionCheckpointStore impl site
// stays where it is and every existing call site keeps working; the
// checkpoint surface itself has one home. This is the mirror of the shipped
// JSONL slice (JsonlCheckpointStore, #1513 / PR #1716), which cut the same
// axis out of the JSONL twin.
//
// Verbatim move: the bodies below are the store's own, unchanged apart from
// the owner-threaded seams, each marked in place:
//
//   * the per-session lock -> _owner.GetSessionLockAsync(...) — the STORE's
//     lock strip, not a copy: a checkpoint write still serializes against a
//     message append on the same session.
//   * the session row read -> _owner.ReadRowAsync(...) — the STORE's read
//     path, so the "session exists" policy is decided in one place.
//   * the connection       -> _owner.OpenConnection() — the STORE's
//     connection string and PRAGMAs; the axis never opens one of its own.
//   * the schema           -> _owner.EnsureInitialized() — the STORE's
//     one-time initialization.
//   * the typed parameters -> SqliteSessionStore.IntParam/StampParam
//     — the STORE's own builders (#1534), so both halves format a column the
//     same way.
//   * the logger           -> the store's own logger, handed over: the log
//     category stays the store's.
//
// Behaviour is unchanged: same SQL, same parameters, same ordering, same
// error mapping, same Result shapes — see
// tests/Harbor.Storage.Tests/SqliteCheckpointsTests.cs and
// tests/Harbor.Storage.Tests/SqliteCheckpointStoreImplSiteTests.cs.
//
// NOT touched here: the append path's known perf problem (#1794 — the
// SqliteAppendScalingGate measures 4.41x on linear code). This file is a
// pure move; the SQL, the parameters and the gate stay exactly as they were.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     The checkpoint/rewind axis of the SQLite backend (#1247), extracted
///     verbatim from <see cref="SqliteSessionStore" /> (#1831, #95 slice: one
///     extract per PR).
/// </summary>
/// <remarks>
///     <para>
///         The axis keeps and numbers the on-disk checkpoint rows, scans them
///         back and rewinds the history to an anchor — while the store keeps
///         the state the axis borrows: its per-session lock strip, its
///         session-row read, its connection and its schema initialization.
///     </para>
///     <para>
///         The store keeps thin forwarders over this type, so the
///         <see cref="ISessionCheckpointStore" /> implementation site is
///         unchanged; the checkpoint SQL has one home.
///     </para>
/// </remarks>
internal sealed class SqliteCheckpointStore : ISessionCheckpointStore
{
    /// <summary>
    ///     The store this axis belongs to: its per-session lock strip, its
    ///     session-row read, its connection and its schema initialization.
    ///     The store's own state is borrowed, never copied.
    /// </summary>
    private readonly SqliteSessionStore _owner;

    /// <summary>
    ///     Logger of the owning store — the log category stays the store's.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     Bind the axis to its store. The store's own state (lock strip,
    ///     session-row read, connection, schema) is borrowed, never copied.
    /// </summary>
    /// <param name="owner">The store that owns the database file.</param>
    /// <param name="logger">Logger of the owning store.</param>
    internal SqliteCheckpointStore(SqliteSessionStore owner, ILogger logger)
    {
        _owner = owner;
        _logger = logger;
    }

    /// <summary>
    ///     Mark a checkpoint over <paramref name="messageId" /> (#1247): the
    ///     anchor's zero-based index is derived by the same ordering
    ///     <see cref="SqliteSessionStore.GetMessagesAsync" /> reads with, and
    ///     the row is inserted under the per-session lock.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Anchor message id to mark.</param>
    /// <param name="label">Optional human label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded checkpoint, or failure if session/message not found.</returns>
    public async Task<Result<SessionCheckpoint>> CreateCheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();

        // #199: the expected "not found" outcomes are decided before the try
        // boundary so they stay plain failures (no throw, no error log).
        var sessionRow = await _owner.ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
            return sessionRow.ConvertFailure<SessionCheckpoint>();
        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<SessionCheckpoint>(SessionStoreErrors.SessionNotFound(sessionId));

        var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                using var conn = _owner.OpenConnection();

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
                insert.Parameters.Add(SqliteSessionStore.IntParam("@idx", index));
                insert.Parameters.Add(SqliteSessionStore.StampParam("@created", checkpoint.CreatedAt));
                insert.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
                insert.ExecuteNonQuery();

                return Result.Success(checkpoint);
            }, ResultErrors.Message)
                .Bind(x => x)
                .TapError(e => SqliteSessionStoreLog.RecordCheckpointFailed(_logger, sessionId, e));
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
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The checkpoints, or failure if the session does not exist.</returns>
    public async Task<Result<IReadOnlyList<SessionCheckpoint>>> ListCheckpointsAsync(
        string sessionId, CancellationToken ct = default)
    {
        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw, no error log).
        var sessionRow = await _owner.ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
            return sessionRow.ConvertFailure<IReadOnlyList<SessionCheckpoint>>();
        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<IReadOnlyList<SessionCheckpoint>>(SessionStoreErrors.SessionNotFound(sessionId));

        return await Result.Try(async () =>
        {
            _owner.EnsureInitialized();
            using var conn = _owner.OpenConnection();
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
    ///     <see cref="SqliteSessionStore.DeleteMessagesAfterAsync" />, checkpoints
    ///     anchored past the anchor go with the dropped future, and a trail marker records
    ///     the rewind — the honest trail. The cleanup and the trail write
    ///     share one transaction, so a crash cannot leave a rewind recorded
    ///     without its dropped checkpoints gone (or the reverse).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="checkpointId">Id of the checkpoint to restore (from <see cref="ListCheckpointsAsync" />).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were dropped and how many remain.</returns>
    public async Task<Result<SessionRewindOutcome>> RewindToCheckpointAsync(
        string sessionId, string checkpointId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        // #199: the expected "not found" outcomes are decided before the try
        // boundary so they stay plain failures (no throw, no error log).
        var sessionRow = await _owner.ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
            return sessionRow.ConvertFailure<SessionRewindOutcome>();
        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
            return Result.Failure<SessionRewindOutcome>(SessionStoreErrors.SessionNotFound(sessionId));

        // Only plain checkpoints are restore points — a trail marker records
        // a past rewind, it is not itself a target (the jsonl backend draws
        // the same line with its !IsRewindTrail filter). Absence rides the
        // Maybe rail until it becomes the shared CheckpointNotFound text,
        // the same ToResult idiom GetAsync uses for a missing session.
        var checkpointRow = await ReadCheckpointRowAsync(sessionId, checkpointId, ct).ConfigureAwait(false);
        if (checkpointRow.IsFailure)
            return checkpointRow.ConvertFailure<SessionRewindOutcome>();
        // guarded: .Value only read after the failure check.
        var checkpointResult = checkpointRow.Value.ToResult(
            SessionStoreErrors.CheckpointNotFound(sessionId, checkpointId));
        if (checkpointResult.IsFailure)
            return checkpointResult.ConvertFailure<SessionRewindOutcome>();
        return await RewindToAnchorAsync(sessionId, checkpointResult.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Truncate to <paramref name="checkpoint" />'s anchor and write the
    ///     honest-trail marker. Kept private and separate so the public verb
    ///     stays a thin validation-and-dispatch seam.
    /// </summary>
    private async Task<Result<SessionRewindOutcome>> RewindToAnchorAsync(
        string sessionId, SessionCheckpoint checkpoint, CancellationToken ct)
    {
        var truncated = await _owner.DeleteMessagesAfterAsync(sessionId, checkpoint.MessageId, ct).ConfigureAwait(false);
        if (truncated.IsFailure)
            return truncated.ConvertFailure<SessionRewindOutcome>();

#pragma warning disable CFE0001
        // CFE0001: false positive — the IsFailure guard above is an early return.
        int removed = truncated.Value;
#pragma warning restore CFE0001

        var trailId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                using var conn = _owner.OpenConnection();
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
                cmd.Parameters.Add(SqliteSessionStore.IntParam("@idx", checkpoint.MessageIndex));
                cmd.Parameters.Add(SqliteSessionStore.StampParam("@created", now));
                cmd.Parameters.AddWithValue("@rewindOf", checkpoint.Id);
                cmd.Parameters.Add(SqliteSessionStore.IntParam("@removed", removed));
                cmd.ExecuteNonQuery();

                cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE session_id = @sid";
                object? count = cmd.ExecuteScalar();
                long remaining = count is long l ? l : 0L;

                tx.Commit();
                return Result.Success(new SessionRewindOutcome(
                    checkpoint.Id, checkpoint.MessageId, removed, (int)remaining));
            }, ResultErrors.Message)
                .Bind(x => x)
                .TapError(e => SqliteSessionStoreLog.RecordRewindFailed(_logger, sessionId, e));
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
            _owner.EnsureInitialized();
            using var conn = _owner.OpenConnection();
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
}
