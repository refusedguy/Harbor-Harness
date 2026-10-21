// SqliteSessionStoreCompaction.cs — the compaction-record axis of the SQLite
// session store (#1767).
//
// The axis sits beside the store, not inside it, for the same reason the
// checkpoint axis does: the store's own file is cited by the failure-text
// parity rule
// (tests/Harbor.Architecture.Tests/SessionStoreFailureTextParityRules.cs),
// and a method inserted in the middle of it moves every citation below the
// insertion point.
//
// THE TABLE
// ---------
// One row per compaction record, in write order. The rowid is the order —
// the same "an append-only log is already monotonic, so the position IS the
// sequence" idea the JSONL twin uses for its line index. The record is NOT a
// message row: it never enters the model context, and it is never counted as
// one.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Sqlite;

// The base list is the store's WHOLE set of seams, and it is repeated here
// verbatim because a partial declaration may not name a different one: the
// store itself names ISessionStore, ISessionCheckpointStore and
// ICompactionEventStore, and a partial type whose parts disagree about their
// base interfaces does not compile.
public sealed partial class SqliteSessionStore : ISessionStore, ISessionCheckpointStore, ICompactionEventStore
{
    /// <summary>
    ///     Append one compaction record to the session's log.
    /// </summary>
    /// <remarks>
    ///     The write rides the store's own per-session lock, so it is ordered
    ///     against the message appends of the same session: a record appended
    ///     while a compaction is in flight lands inside the compaction's
    ///     bracket instead of racing it.
    /// </remarks>
    public async Task<Result> AppendCompactionEventAsync(
        string sessionId,
        SessionCompactionEvent compactionEvent,
        CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();

        // #199: the expected "not found" outcome is decided before the try
        // boundary so it stays a plain failure (no throw, no error log).
        var sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
        {
            return sessionRow.ConvertFailure();
        }

        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
        {
            return Result.Failure(SessionStoreErrors.SessionNotFound(sessionId));
        }

        var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Try(() =>
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  INSERT INTO compaction_events (session_id, kind, turn, error, pruned, tokens_saved, occurred_at, orphaned_turn)
                                  VALUES (@sid, @kind, @turn, @err, @pruned, @tokens, @at, @orphan)
                                  """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@kind", compactionEvent.Kind.ToString());
                cmd.Parameters.AddWithValue("@turn", (object?)compactionEvent.Turn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@err", (object?)compactionEvent.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pruned", compactionEvent.PrunedMessageCount);
                cmd.Parameters.AddWithValue("@tokens", compactionEvent.TokensSaved);
                cmd.Parameters.AddWithValue("@at", Timestamp(compactionEvent.OccurredAt));
                cmd.Parameters.AddWithValue("@orphan", (object?)compactionEvent.OrphanedTurn ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }, ResultErrors.Message)
                .TapError(e => SqliteSessionStoreLog.AppendCompactionEventFailed(_logger, sessionId, e));
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Read the session's compaction records, in write order.
    /// </summary>
    public async Task<Result<IReadOnlyList<SessionCompactionEvent>>> GetCompactionEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionRow.IsFailure)
        {
            return sessionRow.ConvertFailure<IReadOnlyList<SessionCompactionEvent>>();
        }

        if (sessionRow.Value.HasNoValue) // guarded: .Value only read after the failure check.
        {
            return Result.Failure<IReadOnlyList<SessionCompactionEvent>>(
                SessionStoreErrors.SessionNotFound(sessionId));
        }

        return await Result.Try<IReadOnlyList<SessionCompactionEvent>>(async () =>
            {
                EnsureInitialized();
                var events = new List<SessionCompactionEvent>();

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT kind, turn, error, pruned, tokens_saved, occurred_at, orphaned_turn
                                  FROM compaction_events
                                  WHERE session_id = @sid
                                  ORDER BY id ASC
                                  """;
                cmd.Parameters.AddWithValue("@sid", sessionId);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    SessionCompactionEvent? mapped = ReadCompactionRow(reader, events.Count);
                    if (mapped is not null)
                    {
                        events.Add(mapped);
                    }
                }

                return (IReadOnlyList<SessionCompactionEvent>)events;
            }, ResultErrors.Message)
            .TapError(e => SqliteSessionStoreLog.ReadCompactionEventsFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Map one row to the contract record, or null when the row names a
    ///     kind this build does not know.
    /// </summary>
    /// <remarks>
    ///     A kind this build does not know is a record from a newer build. It
    ///     is skipped, not guessed at: reading it as an "end" would release a
    ///     lock that is still held.
    /// </remarks>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="sequence">The record's position among the session's compaction records.</param>
    /// <returns>The contract record, or null for a row this build refuses.</returns>
    private static SessionCompactionEvent? ReadCompactionRow(SqliteDataReader reader, int sequence)
    {
        string kindName = reader.GetString(0);

        if (!TryParseCompactionKind(kindName, out SessionCompactionEventKind kind))
        {
            return null;
        }

        return new SessionCompactionEvent
        {
            Kind = kind,
            Turn = reader.IsDBNull(1) ? null : reader.GetInt32(1),
            Error = reader.IsDBNull(2) ? null : reader.GetString(2),
            PrunedMessageCount = reader.GetInt32(3),
            TokensSaved = reader.GetInt32(4),
            OccurredAt = DateTimeOffset.Parse(reader.GetString(5)),
            Sequence = sequence,
            OrphanedTurn = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        };
    }

    /// <summary>
    ///     Parse a stored kind name. A name this build does not know is a
    ///     REFUSAL, not a default: a record from a newer build is skipped
    ///     rather than read as an "end" that never happened.
    /// </summary>
    /// <remarks>
    ///     Each backend owns its own mapper — the JSONL twin has its own for
    ///     the line shape, this one for the row shape — and the vocabulary
    ///     they share is the kind names. Enum.TryParse also accepts the
    ///     NUMERIC value of a member, so a hand-edited "7" would parse into a
    ///     value this build does not define; IsDefined is the second half of
    ///     the refusal.
    /// </remarks>
    /// <param name="value">The stored name.</param>
    /// <param name="kind">The parsed kind.</param>
    /// <returns>True when the name is one this build knows.</returns>
    private static bool TryParseCompactionKind(string? value, out SessionCompactionEventKind kind)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out kind))
        {
            return false;
        }

        return Enum.IsDefined(kind);
    }
}

/// <summary>
///     The log messages of the compaction axis. They live in the store's own
///     partial so the ids do not collide with the ones the store already
///     declares.
/// </summary>
internal static partial class SqliteSessionStoreLog
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Failed to append a compaction record to session {SessionId}: {Error}")]
    public static partial void AppendCompactionEventFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Failed to read the compaction records of session {SessionId}: {Error}")]
    public static partial void ReadCompactionEventsFailed(ILogger logger, string sessionId, string error);
}
