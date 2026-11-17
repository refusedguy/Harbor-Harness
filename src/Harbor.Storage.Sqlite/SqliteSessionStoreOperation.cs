// SqliteSessionStoreOperation.cs — the fork/turn crash-marker axis of the
// SQLite session store (#2226). Mirrors the compaction axis: its own partial,
// its own table, its own write lock ordering.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Sqlite;

public sealed partial class SqliteSessionStore
{
    /// <summary>Append one fork/turn marker to the session's log.</summary>
    public async Task<Result> AppendOperationEventAsync(
        string sessionId,
        SessionOperationEvent operationEvent,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Result<Maybe<Session>> sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);

        return await sessionRow
            .Bind(row => row.HasNoValue
                ? Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)))
                : InsertOperationEventAsync(sessionId, operationEvent, ct))
            .ConfigureAwait(false);
    }

    private async Task<Result> InsertOperationEventAsync(
        string sessionId,
        SessionOperationEvent operationEvent,
        CancellationToken ct)
    {
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
                                  INSERT INTO operation_events (session_id, kind, owner, turn, boundary_message_id, copied, occurred_at)
                                  VALUES (@sid, @kind, @owner, @turn, @boundary, @copied, @at)
                                  """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@kind", operationEvent.Kind.ToString());
                cmd.Parameters.AddWithValue("@owner", (object?)operationEvent.Owner ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@turn", (object?)operationEvent.Turn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@boundary", (object?)operationEvent.BoundaryMessageId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@copied", operationEvent.Copied);
                cmd.Parameters.AddWithValue("@at", Timestamp(operationEvent.OccurredAt));
                cmd.ExecuteNonQuery();
            }, ResultErrors.Message)
                .TapError(e => SqliteSessionStoreLog.AppendOperationEventFailed(_logger, sessionId, e));
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Read the session's fork/turn markers, in write order.</summary>
    public async Task<Result<IReadOnlyList<SessionOperationEvent>>> GetOperationEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Result<Maybe<Session>> sessionRow = await ReadRowAsync(sessionId, ct).ConfigureAwait(false);

        return await sessionRow
            .Bind(row => row.HasNoValue
                ? Task.FromResult(Result.Failure<IReadOnlyList<SessionOperationEvent>>(
                    SessionStoreErrors.SessionNotFound(sessionId)))
                : ReadOperationRecordsAsync(sessionId, ct))
            .ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyList<SessionOperationEvent>>> ReadOperationRecordsAsync(
        string sessionId,
        CancellationToken ct)
    {
        return await Result.Try<IReadOnlyList<SessionOperationEvent>>(async () =>
            {
                EnsureInitialized();
                var events = new List<SessionOperationEvent>();

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT kind, owner, turn, boundary_message_id, copied, occurred_at
                                  FROM operation_events
                                  WHERE session_id = @sid
                                  ORDER BY id ASC
                                  """;
                cmd.Parameters.AddWithValue("@sid", sessionId);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    SessionOperationEvent? mapped = ReadOperationRow(reader, events.Count);
                    if (mapped is not null)
                    {
                        events.Add(mapped);
                    }
                }

                return (IReadOnlyList<SessionOperationEvent>)events;
            }, ResultErrors.Message)
            .TapError(e => SqliteSessionStoreLog.ReadOperationEventsFailed(_logger, sessionId, e));
    }

    private static SessionOperationEvent? ReadOperationRow(SqliteDataReader reader, int sequence)
    {
        string kindName = reader.GetString(0);
        if (!Enum.TryParse(kindName, ignoreCase: true, out SessionOperationKind kind) || !Enum.IsDefined(kind))
        {
            return null;
        }

        return new SessionOperationEvent
        {
            Kind = kind,
            Owner = reader.IsDBNull(1) ? null : reader.GetString(1),
            Turn = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            BoundaryMessageId = reader.IsDBNull(3) ? null : reader.GetString(3),
            Copied = reader.GetInt32(4),
            OccurredAt = DateTimeOffset.Parse(reader.GetString(5)),
            Sequence = sequence,
        };
    }
}

internal static partial class SqliteSessionStoreLog
{
    [LoggerMessage(EventId = 24, Level = LogLevel.Error, Message = "Failed to append an operation marker to session {SessionId}: {Error}")]
    public static partial void AppendOperationEventFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 25, Level = LogLevel.Error, Message = "Failed to read the operation markers of session {SessionId}: {Error}")]
    public static partial void ReadOperationEventsFailed(ILogger logger, string sessionId, string error);
}
