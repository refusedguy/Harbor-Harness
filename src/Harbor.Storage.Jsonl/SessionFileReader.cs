// SessionFileReader.cs — JSONL session-file read path (header + messages).
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). The store owns the cache + locks; this file owns bytes →
// records: header decode, full-file message parse, and the line classifiers
// the rewrite paths reuse.

using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Read-side of the JSONL session format. Per-line JSON parse errors are
///     aggregated and surfaced via <see cref="ILogger.LogWarning" /> while
///     still returning the successfully deserialized messages (§ROP-001).
/// </summary>
internal static class SessionFileReader
{
    /// <summary>
    ///     How many times a read retries after detecting that the file changed
    ///     under it. One retry absorbs the ordinary "an append landed between
    ///     the stat and the read" race; the bound is what stops a writer that
    ///     outpaces the reader from spinning here — the last attempt is returned
    ///     as an explicitly unstable snapshot that the caller must not cache
    ///     (#459).
    /// </summary>
    private const int MaxSnapshotAttempts = 3;

    /// <summary>
    ///     True when the record is a <c>"message"</c> entry carrying the given
    ///     id. Header lines and unparseable records are never matched, so a
    ///     rewrite cannot accidentally drop them. Malformed lines are left
    ///     untouched on disk — the read path already reports them as parse
    ///     warnings.
    /// </summary>
    /// <remarks>
    ///     <paramref name="idNeedle" /> is the UTF-8 form of
    ///     <c>"id":"&lt;messageId&gt;"</c>, built once per rewrite. Searching
    ///     the raw bytes is what keeps a streaming rewrite from decoding every
    ///     record into a <see cref="string" /> just to answer this question.
    /// </remarks>
    internal static bool IsMessageEntryWithId(ReadOnlySpan<byte> record, string messageId, byte[] idNeedle)
    {
        // (ReadOnlySpan<byte>) so this binds the sequence overload below rather
        // than a byte[] one — same helper the "type" probes use.
        if (record.IndexOf((ReadOnlySpan<byte>)idNeedle) < 0)
        {
            return false;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.MessageEntry);
            return entry is { Type: "message", Id: var id } && id == messageId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when the line is a session header (<c>"type":"session"</c>).</summary>
    internal static bool IsSessionHeaderLine(ReadOnlySpan<byte> line)
    {
        return line.IndexOf("\"type\":\"session\""u8) >= 0;
    }

    /// <summary>
    ///     True when the line is a checkpoint marker (<c>"type":"checkpoint"</c>).
    ///     Pure byte probe, same as <see cref="IsSessionHeaderLine" />: a
    ///     message payload carrying the literal is JSON-escaped
    ///     (<c>\"type\":\"checkpoint\"</c>), so the unescaped probe only
    ///     matches structural lines. Checkpoints are metadata, not history —
    ///     the read path drops them here instead of routing them to the
    ///     message parser (which would warn per line, #1247 slice 1).
    /// </summary>
    internal static bool IsCheckpointLine(ReadOnlySpan<byte> line)
    {
        return line.IndexOf("\"type\":\"checkpoint\""u8) >= 0;
    }

    /// <summary>True when the line is a <c>"message"</c> entry with any id.</summary>
    internal static bool IsAnyMessageEntry(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf("\"type\":\"message\""u8) < 0)
        {
            return false;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.MessageEntry);
            return entry is { Type: "message" };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Parse the JSONL session file from disk into a chronological message
    ///     list, together with the <see cref="SessionFileStat" /> the parse was
    ///     taken from and whether that pairing is provable. Per-line JSON parse
    ///     errors are aggregated (bounded — see <see cref="ParseErrors" />) and
    ///     surfaced via <see cref="ILogger.LogWarning" />, while still
    ///     returning the successfully deserialized messages (§ROP-001
    ///     resolved).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Perf sprint (PERF-005 successor):</b> the file is read once
    ///         into a pooled buffer and parsed line-by-line over raw UTF-8
    ///         spans via <see cref="JsonlLineParser" /> — no per-line
    ///         <see cref="string" />, no <c>Encoding.UTF8.GetBytes</c>, no
    ///         <see cref="JsonElement"/> round-trip for payloads. Allocations
    ///         are limited to the returned message object graph.
    ///     </para>
    ///     <para>
    ///         <b>Snapshot integrity (#459):</b> the read is bounded by the
    ///         length measured on the <em>open handle</em> — never by the
    ///         pooled buffer's capacity and never by a stat taken before the
    ///         open. The old shape measured a <see cref="FileInfo" /> length,
    ///         then read <c>while (read &lt; buffer.Length)</c>; since the pool
    ///         returns an array <em>larger</em> than requested, an append landing
    ///         in that window extended the read past the measured end and cut
    ///         the new record mid-line. The truncated line was then merely
    ///         <c>LogWarning</c>ed, and the short list was published to the
    ///         store's cache as if it were the whole session.
    ///     </para>
    ///     <para>
    ///         <b>Bounded allocation (#460):</b> the buffer used to be
    ///         <c>ArrayPool&lt;byte&gt;.Shared.Rent((int)fileLength)</c>, so a
    ///         300 MB session bought a 300 MB LOH array on every cache miss and
    ///         the "~2 GiB" guard in front of it was a size the pool cannot
    ///         serve anyway. The file is now streamed through
    ///         <see cref="ChunkedLineReader" />: one pooled
    ///         <see cref="ChunkedLineReader.ChunkBytes" /> block plus the record
    ///         being assembled, with a hard
    ///         <see cref="ChunkedLineReader.MaxRecordBytes" /> ceiling per
    ///         record. The measured length still bounds how much is
    ///         <em>read</em> — it is what makes the snapshot provable — it no
    ///         longer decides how much is held.
    ///     </para>
    /// </remarks>
    /// <param name="sessionFile">Absolute path to the .jsonl file.</param>
    /// <param name="sessionId">The session id (passed through to the parser).</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    /// <param name="ct">Cancellation token observed by the file read.</param>
    /// <returns>
    ///     The chronological message list plus its provenance, or failure when
    ///     the file is too large to read at all.
    /// </returns>
    internal static async Task<Result<MessageReadSnapshot>> ParseMessagesFromDiskAsync(
        string sessionFile,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        var messages = new Dictionary<string, AgentMessage>();
        var errors = new ParseErrors();

        // Taken BEFORE the open: paired with the post-read stat it proves the
        // bytes we parsed are exactly the file state this stat names.
        var stat = SessionFileStat.Read(sessionFile);
        if (stat.Length > int.MaxValue)
            return TooLarge<MessageReadSnapshot>(stat.Length);

        bool stable = false;
        bool sawOversized = false;
        long lastRead = 0;
        long budget = 0;

        // A single concurrent append is the common case and the retry resolves
        // it: the next attempt measures the grown file and reads it whole. The
        // bound keeps a writer streaming faster than we read from turning into
        // an unbounded spin — the last attempt is returned as an explicitly
        // unstable snapshot instead.
        for (int attempt = 1; attempt <= MaxSnapshotAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Re-measure every attempt; the previous one may be stale.
            stat = SessionFileStat.Read(sessionFile);
            if (stat.Length > int.MaxValue)
                return TooLarge<MessageReadSnapshot>(stat.Length);

            // Only the last attempt's records are kept, and the previous
            // attempt's are dropped here rather than folded in — the parse is
            // the expensive half, so a retry re-does it, exactly as it re-did
            // the read before.
            messages.Clear();
            errors.Clear();
            sawOversized = false;

            using (var fs = new FileStream(
                       sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 64 * 1024, useAsync: true))
            {
                // Budget from the OPEN handle, not from a pre-open stat. It
                // bounds the read; ChunkedLineReader bounds the memory.
                budget = fs.Length;
                if (budget > int.MaxValue)
                    return TooLarge<MessageReadSnapshot>(budget);

                using var reader = new ChunkedLineReader(fs) { ByteCeiling = budget };
                while (await reader.FillAsync(ct).ConfigureAwait(false))
                {
                    FoldRecords(reader, sessionId, messages, errors);
                }

                FoldTrailingRecord(reader, sessionId, messages, errors);
                lastRead = reader.BytesRead;
                sawOversized = reader.SawOversizedRecord;
            }

            // Unchanged mtime across the read, unchanged length, and every
            // measured byte actually read == the parsed content is the whole
            // file for that stat. An append moves the length; an atomic
            // rewrite (temp + rename) moves the path's mtime. A short read
            // means the file shrank under us, and what we hold is not it.
            var after = SessionFileStat.Read(sessionFile);
            stable = lastRead == budget && after.Matches(stat) && after.Length == budget;
            if (stable)
                break;
        }

        if (errors.Total > 0)
        {
            logger.LogWarning("Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}",
                errors.Total, sessionId, errors.Summary());
        }

        if (sawOversized)
        {
            logger.LogWarning(
                "Session {SessionId} holds a record over the {MaxBytes} byte ceiling; " +
                "it was skipped and the rest of the session was read.",
                sessionId, ChunkedLineReader.MaxRecordBytes);
        }

        if (!stable)
        {
            // Loud, because the caller is deliberately NOT caching this and
            // the next read will re-parse.
            logger.LogWarning(
                "Session {SessionId} was being appended to while it was read; returning {MessageCount} message(s) from a partial snapshot (not cached).",
                sessionId, messages.Count);
        }

        var ordered = messages.Values.OrderBy(m => m.CreatedAt).ToList();
        return Result.Success<MessageReadSnapshot>(new MessageReadSnapshot(ordered, stat, stable));
    }

    private static Result<T> TooLarge<T>(long length) =>
        Result.Failure<T>($"Session file too large ({length} bytes); refusing unbounded read.");

    /// <summary>
    ///     Folds every complete record the reader's current block holds. Split
    ///     out of the async method on purpose: a <c>ReadOnlySpan&lt;byte&gt;</c>
    ///     local cannot be declared inside an async method (CS4012), and the
    ///     parser wants one per record.
    /// </summary>
    private static void FoldRecords(
        ChunkedLineReader reader,
        string sessionId,
        Dictionary<string, AgentMessage> messages,
        ParseErrors errors)
    {
        while (reader.TryGetRecord(out var record))
        {
            FoldRecord(record, sessionId, messages, errors);
        }
    }

    /// <summary>
    ///     Folds the last record of a file that does not end in LF — the same
    ///     record the single-buffer reader used to pick up with
    ///     <c>nl &lt; 0 ? rest : …</c>.
    /// </summary>
    private static void FoldTrailingRecord(
        ChunkedLineReader reader,
        string sessionId,
        Dictionary<string, AgentMessage> messages,
        ParseErrors errors)
    {
        if (reader.TryGetTrailingRecord(out var record))
        {
            FoldRecord(record, sessionId, messages, errors);
        }
    }

    /// <summary>
    ///     Fold one record into <paramref name="messages" /> (latest id wins).
    ///     Header lines and blank lines are dropped; a per-line parse failure is
    ///     routed to <paramref name="errors" /> and the record is skipped.
    /// </summary>
    private static void FoldRecord(
        ReadOnlySpan<byte> record,
        string sessionId,
        Dictionary<string, AgentMessage> messages,
        ParseErrors errors)
    {
        if (record.IsEmpty || record.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
        {
            return;
        }

        if (IsSessionHeaderLine(record))
        {
            return;
        }

        if (IsCheckpointLine(record))
        {
            return;
        }

        var msgResult = JsonlLineParser.Parse(record, sessionId);
        if (msgResult.IsSuccess)
        {
            messages[msgResult.Value.Id] = msgResult.Value;
        }
        else
        {
            errors.Add($"Line parse failed: {msgResult.Error}");
        }
    }

    /// <summary>
    ///     Bounded collector for per-line parse failures. A corrupt or crafted
    ///     file is nothing <em>but</em> parse failures, and one string per
    ///     failed line puts the unbounded allocation straight back — so the
    ///     first <see cref="MaxRecorded" /> are kept verbatim and the rest are
    ///     only counted, and the warning carries the total either way.
    /// </summary>
    private sealed class ParseErrors
    {
        private const int MaxRecorded = 20;

        private readonly List<string> _recorded = [];

        /// <summary>Every failure seen, recorded or not.</summary>
        internal int Total { get; private set; }

        internal void Clear()
        {
            Total = 0;
            _recorded.Clear();
        }

        internal void Add(string error)
        {
            Total++;
            if (_recorded.Count < MaxRecorded)
            {
                _recorded.Add(error);
            }
        }

        /// <summary>
        ///     The recorded failures joined, with a count of the overflow when
        ///     there was one. For a file with at most <see cref="MaxRecorded" />
        ///     bad lines this is exactly the old message, unchanged.
        /// </summary>
        internal string Summary() =>
            Total <= _recorded.Count
                ? string.Join("; ", _recorded)
                : $"{string.Join("; ", _recorded)} (+{Total - _recorded.Count} more)";
    }

    /// <summary>
    ///     Decode the line-1 session header. Every expected bad state (empty
    ///     file, blank first line, unparseable JSON, null record) is a
    ///     <see cref="Result.Failure{T}" /> naming the session, the reason, and
    ///     the file path (#199) — never a null that the caller must remember
    ///     to check. Cancellation still propagates via
    ///     <see cref="Harbor.Abstractions.Results.ResultErrors.Message" />.
    /// </summary>
    internal static async Task<Result<SessionHeaderEntry>> TryReadHeaderAsync(
        string path, string sessionId, CancellationToken ct)
    {
        // Result.Try (CSharpFunctionalExtensions 3.7.0) is the library form of
        // wrapping a throwing call in a Result. ResultErrors.Message rethrows
        // OperationCanceledException inside the handler, so cancellation still
        // propagates exactly as before.
        var readResult = await Result.Try(
                async () =>
                {
                    using var reader = new StreamReader(path);
                    return await reader.ReadLineAsync(ct).ConfigureAwait(false);
                },
                ex => $"Session '{sessionId}' header unreadable ({ResultErrors.Message(ex)}): {path}.")
            .ConfigureAwait(false);

        if (readResult.IsFailure)
            return readResult.ConvertFailure<SessionHeaderEntry>();
        string? firstLine = readResult.Value;

        if (firstLine is null)
            return Result.Failure<SessionHeaderEntry>($"Session '{sessionId}' is empty: {path}.");
        if (string.IsNullOrWhiteSpace(firstLine))
            return Result.Failure<SessionHeaderEntry>(
                $"Session '{sessionId}' is corrupt (blank header line): {path}.");

        return Result.Try(
                () => JsonSerializer.Deserialize<SessionHeaderEntry>(
                    firstLine, JsonlCodecContext.Default.SessionHeaderEntry),
                ex => $"Session '{sessionId}' is corrupt (header parse failed: {ex.Message}): {path}.")
            .Bind(header => header is not null
                ? Result.Success(header)
                : Result.Failure<SessionHeaderEntry>(
                    $"Session '{sessionId}' is corrupt (header deserialized to null): {path}."));
    }
}
