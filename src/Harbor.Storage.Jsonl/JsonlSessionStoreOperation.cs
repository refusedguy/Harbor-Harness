// JsonlSessionStoreOperation.cs — the fork/turn crash-marker axis of the JSONL
// session store (#2226).
//
// It sits beside the store for the same reason the compaction and feedback
// axes do: those methods live in their own partials so a method inserted in
// the middle of the store's file does not move every failure-text citation
// below it.

using System.Text.Json;
using Harbor.Storage.Shared;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Jsonl;

public sealed partial class JsonlSessionStore
{
    /// <summary>Append one fork/turn marker to the session's log.</summary>
    public Task<Result> AppendOperationEventAsync(
        string sessionId,
        SessionOperationEvent operationEvent,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId)
            .Bind(sessionFile => AppendOperationEventToFileAsync(sessionId, sessionFile, operationEvent, ct));
    }

    private async Task<Result> AppendOperationEventToFileAsync(
        string sessionId,
        string sessionFile,
        SessionOperationEvent operationEvent,
        CancellationToken ct)
    {
        if (!File.Exists(sessionFile))
        {
            return Result.Failure(SessionStoreErrors.SessionNotFound(sessionId));
        }

        return await Result.Try(async () =>
        {
            var semaphore = await GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                SessionOperationEntry entry = SessionOperationMapperKit.ToRow(operationEvent);
                byte[] line = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.SessionOperationEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .TapError(e => JsonlSessionStoreLog.AppendOperationEventFailed(_logger, sessionId, e));
    }

    /// <summary>Read the session's fork/turn markers, in write order.</summary>
    public Task<Result<IReadOnlyList<SessionOperationEvent>>> GetOperationEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId)
            .Bind(sessionFile => ReadOperationEventsFromFileAsync(sessionId, sessionFile, _logger));
    }

    private static Task<Result<IReadOnlyList<SessionOperationEvent>>> ReadOperationEventsFromFileAsync(
        string sessionId,
        string sessionFile,
        ILogger logger)
    {
        if (!File.Exists(sessionFile))
        {
            return Task.FromResult(
                Result.Failure<IReadOnlyList<SessionOperationEvent>>(SessionStoreErrors.SessionNotFound(sessionId)));
        }

        return Task.FromResult(
            Result.Try<IReadOnlyList<SessionOperationEvent>>(() =>
                {
                    var events = new List<SessionOperationEvent>();
                    ReadOperationRecords(sessionFile, sessionId, events, logger);
                    return (IReadOnlyList<SessionOperationEvent>)events;
                }, ResultErrors.Message)
                .TapError(e => JsonlSessionStoreLog.ReadOperationEventsFailed(logger, sessionId, e)));
    }

    private static void ReadOperationRecords(
        string sessionFile,
        string sessionId,
        List<SessionOperationEvent> events,
        ILogger logger)
    {
        using var source = new FileStream(
            sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        using var reader = new ChunkedLineReader(source);
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                FoldOperationRecord(record, sessionId, events, logger);
            }
        }

        if (reader.TryGetTrailingRecord(out var trailing))
        {
            FoldOperationRecord(trailing, sessionId, events, logger);
        }
    }

    private static void FoldOperationRecord(
        ReadOnlySpan<byte> record,
        string sessionId,
        List<SessionOperationEvent> events,
        ILogger logger)
    {
        if (!SessionFileReader.IsOperationLine(record))
        {
            return;
        }

        SessionOperationEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.SessionOperationEntry);
        }
        catch (JsonException ex)
        {
            JsonlSessionStoreLog.SkippingMalformedOperationLine(logger, ex, sessionId);
            return;
        }

        if (entry is not { Type: "operation" })
        {
            return;
        }

        if (!SessionOperationMapperKit.TryParseKind(entry.Kind, out SessionOperationKind kind))
        {
            JsonlSessionStoreLog.SkippingUnknownOperationKind(logger, sessionId, entry.Kind);
            return;
        }

        events.Add(SessionOperationMapperKit.ToContract(entry, kind, events.Count));
    }
}

internal static partial class JsonlSessionStoreLog
{
    [LoggerMessage(EventId = 24, Level = LogLevel.Error, Message = "Failed to append an operation marker to session {SessionId}: {Error}")]
    public static partial void AppendOperationEventFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 25, Level = LogLevel.Error, Message = "Failed to read the operation markers of session {SessionId}: {Error}")]
    public static partial void ReadOperationEventsFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Skipping malformed operation line in session {SessionId}.")]
    public static partial void SkippingMalformedOperationLine(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 27, Level = LogLevel.Warning, Message = "Skipping operation line in session {SessionId}: unknown kind '{Kind}'.")]
    public static partial void SkippingUnknownOperationKind(ILogger logger, string sessionId, string kind);
}
