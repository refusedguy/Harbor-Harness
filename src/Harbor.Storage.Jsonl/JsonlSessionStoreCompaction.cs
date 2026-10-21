// JsonlSessionStoreCompaction.cs — the compaction-record axis of the JSONL
// session store (#1767).
//
// The axis sits beside the store, not inside it, for the same reason the
// checkpoint axis and the feedback axis do: the store's own file is cited by
// the failure-text parity rule
// (tests/Harbor.Architecture.Tests/SessionStoreFailureTextParityRules.cs),
// and a method inserted in the middle of it moves every citation below the
// insertion point.
//
// WHAT THE FILE NOW HOLDS
// -----------------------
//   {"type":"session",     …}   the header
//   {"type":"message",     …}   the history
//   {"type":"checkpoint",  …}   the rewind markers
//   {"type":"messageRating", …} / {"type":"sessionRemark", …}   the feedback
//   {"type":"compaction",  …}   the compaction lock and its audit trail
//
// The compaction lines are written under the SAME per-session lock the message
// appends use, so a record appended while a compaction is in flight lands
// inside the compaction's bracket instead of racing it.

using System.Text.Json;
using Harbor.Storage.Shared;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Jsonl;

public sealed partial class JsonlSessionStore
{
    /// <summary>
    ///     Append one compaction record to the session's log.
    /// </summary>
    /// <remarks>
    ///     The write rides the session's own lock, so it is ordered against
    ///     the message appends: a compaction record never interleaves inside
    ///     another write.
    /// </remarks>
    public async Task<Result> AppendCompactionEventAsync(
        string sessionId,
        SessionCompactionEvent compactionEvent,
        CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy — an Esc must
        // never surface as "session not found".
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
        {
            return resolved.ConvertFailure();
        }

        string sessionFile = resolved.Value;
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

                // #1740: the keyless tier scrubs the text-bearing fields on
                // the way out. The failure text of a compaction is exactly
                // such a field — it can quote a provider error that quotes a
                // credential.
                SessionCompactionEvent scrubbed = compactionEvent.Error is null
                    ? compactionEvent
                    : compactionEvent with { Error = ScrubTextForWrite(compactionEvent.Error) };

                var entry = SessionCompactionMapperKit.ToRow(scrubbed);

                byte[] line = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.SessionCompactionEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .TapError(e => JsonlSessionStoreLog.AppendCompactionEventFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Read the session's compaction records, in write order.
    /// </summary>
    /// <remarks>
    ///     The scan is a byte-level pass over the file, the same shape the
    ///     feedback axis uses: the compaction lines are picked out of the
    ///     record stream and decoded, and every other line is not this
    ///     method's business.
    /// </remarks>
    public Task<Result<IReadOnlyList<SessionCompactionEvent>>> GetCompactionEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
        {
            return Task.FromResult(resolved.ConvertFailure<IReadOnlyList<SessionCompactionEvent>>());
        }

        string sessionFile = resolved.Value;
        if (!File.Exists(sessionFile))
        {
            return Task.FromResult(
                Result.Failure<IReadOnlyList<SessionCompactionEvent>>(SessionStoreErrors.SessionNotFound(sessionId)));
        }

        return Task.FromResult(
            Result.Try<IReadOnlyList<SessionCompactionEvent>>(() =>
                {
                    var events = new List<SessionCompactionEvent>();
                    ReadCompactionRecords(sessionFile, sessionId, events, _logger);
                    return (IReadOnlyList<SessionCompactionEvent>)events;
                }, ResultErrors.Message)
                .TapError(e => JsonlSessionStoreLog.ReadCompactionEventsFailed(_logger, sessionId, e)));
    }

    /// <summary>
    ///     Scan the session file for the compaction lines and fold them into
    ///     <paramref name="events" />, in write order.
    /// </summary>
    /// <remarks>
    ///     A malformed compaction line warns and is skipped (§ROP-001): one
    ///     bad line never costs the session its whole lock history. The
    ///     sequence of each record is its position among the compaction
    ///     records, so a reader can see the order the lock was taken and
    ///     released in.
    /// </remarks>
    private static void ReadCompactionRecords(
        string sessionFile,
        string sessionId,
        List<SessionCompactionEvent> events,
        ILogger logger)
    {
        using var source = new FileStream(
            sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        using var reader = new ChunkedLineReader(source);
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                FoldCompactionRecord(record, sessionId, events, logger);
            }
        }

        if (reader.TryGetTrailingRecord(out var trailing))
        {
            FoldCompactionRecord(trailing, sessionId, events, logger);
        }
    }

    /// <summary>
    ///     Fold one record into the compaction history. A record that is not a
    ///     compaction line is not this method's business.
    /// </summary>
    /// <param name="record">The record to fold.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="events">The events collected so far.</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    private static void FoldCompactionRecord(
        ReadOnlySpan<byte> record,
        string sessionId,
        List<SessionCompactionEvent> events,
        ILogger logger)
    {
        if (!SessionFileReader.IsCompactionLine(record))
        {
            return;
        }

        SessionCompactionEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.SessionCompactionEntry);
        }
        catch (JsonException ex)
        {
            JsonlSessionStoreLog.SkippingMalformedCompactionLine(logger, ex, sessionId);
            return;
        }

        if (entry is not { Type: "compaction" })
        {
            return;
        }

        // A kind this build does not know is a record from a newer build. It
        // is skipped, not guessed at: reading it as an "end" would release a
        // lock that is still held.
        if (!SessionCompactionMapperKit.TryParseKind(entry.Kind, out SessionCompactionEventKind kind))
        {
            JsonlSessionStoreLog.SkippingUnknownCompactionKind(logger, sessionId, entry.Kind);
            return;
        }

        events.Add(SessionCompactionMapperKit.ToContract(entry, kind, events.Count));
    }
}

/// <summary>
///     The log messages of the compaction axis. They live in the store's own
///     partial so the ids do not collide with the ones the store already
///     declares.
/// </summary>
internal static partial class JsonlSessionStoreLog
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Failed to append a compaction record to session {SessionId}: {Error}")]
    public static partial void AppendCompactionEventFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Failed to read the compaction records of session {SessionId}: {Error}")]
    public static partial void ReadCompactionEventsFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Skipping malformed compaction line in session {SessionId}.")]
    public static partial void SkippingMalformedCompactionLine(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Skipping compaction line in session {SessionId}: unknown kind '{Kind}'.")]
    public static partial void SkippingUnknownCompactionKind(ILogger logger, string sessionId, string kind);
}
