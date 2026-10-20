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
    ///         <b>Ordering without a blind sort (#1533):</b> the reader used to
    ///         finish every parse with
    ///         <c>messages.Values.OrderBy(m =&gt; m.CreatedAt).ToList()</c> —
    ///         O(n log n) sorting machinery and a second full list on every
    ///         cache miss, including the cache-miss-per-appended-turn steady
    ///         state. The parse now collects records in arrival order
    ///         (<see cref="MessageInbox" />) and pays the stable
    ///         <c>CreatedAt</c> sort exactly once — only when a comparison
    ///         pass proves the log was not append-linear.
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
        // #1586 (Decorator): the bounded retry over a moving file is
        // SessionSnapshotRetryPolicy's job — attempt counting, the
        // inter-attempt delay and the last-attempt passthrough. This method
        // owns the single pass (ReadSnapshotOnceAsync) plus the
        // once-per-winning-attempt logging and ordering below. A failure (e.g.
        // an unbounded file) is complete on arrival — retrying cannot fix it,
        // so it is returned immediately.
        Result<SnapshotAttempt> attempt = await SessionSnapshotRetryPolicy.ExecuteAsync(
            attempt: innerCt => ReadSnapshotOnceAsync(sessionFile, sessionId, innerCt),
            isComplete: result => result.Match(won => won.Snapshot.IsStable, _ => true),
            ct).ConfigureAwait(false);

        if (attempt.IsFailure)
            return attempt.ConvertFailure<MessageReadSnapshot>();

        return attempt.Map(won =>
        {
            if (won.ErrorTotal > 0)
            {
                SessionFileReaderLog.MalformedLinesEncountered(logger, won.ErrorTotal, sessionId, won.ErrorSummary);
            }

            if (won.SawOversized)
            {
                SessionFileReaderLog.OversizedRecordSkipped(logger, sessionId, ChunkedLineReader.MaxRecordBytes);
            }

            if (won.Snapshot.IsTailTruncated)
            {
                // #1744: the tail is the shape a killed process leaves — the
                // write was cut in the middle of the last record. It is named
                // apart from the general malformed-line warning, because "the
                // session was cut short" and "some record in the middle is
                // corrupt" call for different repairs.
                SessionFileReaderLog.TruncatedTailSkipped(logger, sessionId);
            }

            if (!won.Snapshot.IsStable)
            {
                // Loud, because the caller is deliberately NOT caching this and
                // the next read will re-parse.
                SessionFileReaderLog.PartialSnapshotReturned(logger, sessionId, won.Snapshot.Messages.Count);
            }

            return won.Snapshot;
        });
    }

    /// <summary>
    ///     One pass over the session file: measure, read within the open
    ///     handle's budget, parse, and prove the pairing. Never retries — the
    ///     <see cref="SessionSnapshotRetryPolicy" /> decorator decides that.
    /// </summary>
    private static async Task<Result<SnapshotAttempt>> ReadSnapshotOnceAsync(
        string sessionFile,
        string sessionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Re-measured every attempt; the previous one may be stale. Taken
        // BEFORE the open: paired with the post-read stat it proves the bytes
        // we parsed are exactly the file state this stat names.
        var stat = SessionFileStat.Read(sessionFile);
        if (stat.Length > int.MaxValue)
            return TooLarge<SnapshotAttempt>(stat.Length);

        var inbox = new MessageInbox();
        var errors = new ParseErrors();

        long lastRead;
        long budget;
        bool sawOversized;
        bool sawOversizedTail;
        TrailingRecord tail;
        using (var fs = new FileStream(
                   sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                   bufferSize: 64 * 1024, useAsync: true))
        {
            // Budget from the OPEN handle, not from a pre-open stat. It
            // bounds the read; ChunkedLineReader bounds the memory.
            budget = fs.Length;
            if (budget > int.MaxValue)
                return TooLarge<SnapshotAttempt>(budget);

            using var reader = new ChunkedLineReader(fs) { ByteCeiling = budget };
            while (await reader.FillAsync(ct).ConfigureAwait(false))
            {
                FoldRecords(reader, sessionId, inbox, errors);
            }

            tail = FoldTrailingRecord(reader, sessionId, inbox, errors);
            lastRead = reader.BytesRead;
            sawOversized = reader.SawOversizedRecord;
            sawOversizedTail = reader.SawOversizedTail;
        }

        // Unchanged mtime across the read, unchanged length, and every
        // measured byte actually read == the parsed content is the whole
        // file for that stat. An append moves the length; an atomic
        // rewrite (temp + rename) moves the path's mtime. A short read
        // means the file shrank under us, and what we hold is not it.
        var after = SessionFileStat.Read(sessionFile);
        bool stable = lastRead == budget && after.Matches(stat) && after.Length == budget;

        // #1744: the read counts what it could NOT deliver. A record whose
        // bytes are not a complete document is skipped — there is nothing to
        // salvage from half a JSON object — but the skip is counted and the
        // tail case is named, so it never reads as "the agent never said
        // that". The oversized record counts as one unreadable record: it
        // was dropped, and a dropped record is a record the caller does not
        // have.
        int unreadable = errors.Total + (sawOversized ? 1 : 0);
        bool tailTruncated = tail == TrailingRecord.Unreadable || sawOversizedTail;

        var snapshot = new MessageReadSnapshot(
            inbox.ToChronological(),
            stat,
            stable,
            inbox.Header,
            unreadable,
            tailTruncated);
        return Result.Success(new SnapshotAttempt(snapshot, errors.Total, errors.Summary(), sawOversized));
    }

    /// <summary>
    ///     What the last record of the file was — the record that has no
    ///     terminator behind it, if the file has one.
    /// </summary>
    private enum TrailingRecord
    {
        /// <summary>The file ended with a terminator: there is no trailing record.</summary>
        None,

        /// <summary>The trailing record decoded.</summary>
        Decoded,

        /// <summary>
        ///     The trailing record did not decode: the write was cut at the
        ///     tail of the log, which is the shape a killed process leaves.
        /// </summary>
        Unreadable,
    }

    /// <summary>
    ///     One attempt's outcome: the snapshot plus the diagnostics the outer
    ///     method logs once for the winning attempt. Only the last attempt's
    ///     records are ever kept — the parse is the expensive half, so a retry
    ///     re-does it, exactly as it re-did the read before.
    /// </summary>
    private sealed record SnapshotAttempt(
        MessageReadSnapshot Snapshot,
        int ErrorTotal,
        string ErrorSummary,
        bool SawOversized);

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
        MessageInbox inbox,
        ParseErrors errors)
    {
        while (reader.TryGetRecord(out var record))
        {
            FoldRecord(record, sessionId, inbox, errors);
        }
    }

    /// <summary>
    ///     Folds the last record of a file that does not end in LF — the same
    ///     record the single-buffer reader used to pick up with
    ///     <c>nl &lt; 0 ? rest : …</c>.
    /// </summary>
    /// <remarks>
    ///     The record that has no terminator behind it is the one a killed
    ///     process leaves half-written, so its fate is reported separately
    ///     (#1744): a trailing record that does not decode is a truncated TAIL,
    ///     which is a different fact from a broken record somewhere in the
    ///     middle of the log.
    /// </remarks>
    private static TrailingRecord FoldTrailingRecord(
        ChunkedLineReader reader,
        string sessionId,
        MessageInbox inbox,
        ParseErrors errors)
    {
        if (!reader.TryGetTrailingRecord(out var record))
        {
            return TrailingRecord.None;
        }

        return FoldRecord(record, sessionId, inbox, errors)
            ? TrailingRecord.Decoded
            : TrailingRecord.Unreadable;
    }

    /// <summary>
    ///     Fold one record into <paramref name="inbox" /> (latest id wins).
    ///     Header lines and blank lines are dropped; a per-line parse failure is
    ///     routed to <paramref name="errors" /> and the record is skipped.
    /// </summary>
    /// <returns>
    ///     True when the record decoded into a message. False for a record that
    ///     was dropped — a parse failure, a header, a checkpoint, a blank line
    ///     — so the caller can tell "the tail did not decode" from "the tail
    ///     was not a message at all" (#1744).
    /// </returns>
    private static bool FoldRecord(
        ReadOnlySpan<byte> record,
        string sessionId,
        MessageInbox inbox,
        ParseErrors errors)
    {
        if (record.IsEmpty || record.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
        {
            return true;
        }

        if (IsSessionHeaderLine(record))
        {
            // #1785: the header is captured, not just dropped — the export
            // needs it from the SAME pass as the history (a second read would
            // be a second generation). A second header line is a leftover, not
            // a second generation: the first one wins.
            inbox.CaptureHeader(record);
            return true;
        }

        if (IsCheckpointLine(record))
        {
            return true;
        }

        var msgResult = JsonlLineParser.Parse(record, sessionId);
        if (msgResult.IsSuccess)
        {
            inbox.Add(msgResult.Value);
            return true;
        }

        errors.Add($"Line parse failed: {msgResult.Error}");
        return false;
    }

    /// <summary>
    ///     Arrival-order store for a read: the collector behind the JSONL
    ///     messages → snapshot pipeline.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#1533 — the read path does not sort what is already ordered.
    ///         </b> The collector that replaced the
    ///         <c>Dictionary&lt;string, AgentMessage&gt;</c> keeps records in
    ///         arrival order and remembers each id's slot, so a repeated id
    ///         replaces its value IN PLACE — the same "latest id wins, the
    ///         first-occurrence position counts" semantics the dictionary used
    ///         to feed <c>Values.OrderBy(m =&gt; m.CreatedAt)</c> with. A JSONL
    ///         session file is an append-linear log, so the arrival order is
    ///         the chronological order almost always: proving that is one
    ///         comparison pass over the collected list, and the stable
    ///         <c>OrderBy</c> sort runs exactly once, on the first
    ///         provably-out-of-order read — instead of paying
    ///         O(n log n) machinery plus a fresh list on every cache miss.
    ///     </para>
    ///     <para>
    ///         The list this collector returns is privately owned — built by
    ///         this read, handed to the <see cref="MessageReadSnapshot" />, and
    ///         never re-sorted in place afterwards.
    ///     </para>
    /// </remarks>
    private sealed class MessageInbox
    {
        private readonly List<AgentMessage> _arrival = [];
        private readonly Dictionary<string, int> _slots = new();
        private bool _headerSeen;

        /// <summary>Records one parsed message; a repeated id replaces its value in place.</summary>
        internal void Add(AgentMessage message)
        {
            if (_slots.TryGetValue(message.Id, out int slot))
            {
                _arrival[slot] = message;
                return;
            }

            _slots.Add(message.Id, _arrival.Count);
            _arrival.Add(message);
        }

        /// <summary>
        ///     The file's header record, captured from the SAME pass as the
        ///     messages (<b>#1785</b>). Null when the file carries no readable
        ///     header — the caller decides what that means.
        /// </summary>
        internal SessionHeaderEntry? Header { get; private set; }

        /// <summary>
        ///     Capture the header line. Only the FIRST header line counts: a
        ///     second one is a leftover of a rewritten file, not a second
        ///     generation, and taking the later one would mix generations
        ///     inside a single read — the very defect #1785 is about.
        /// </summary>
        internal void CaptureHeader(ReadOnlySpan<byte> record)
        {
            if (_headerSeen)
            {
                return;
            }

            _headerSeen = true;
            try
            {
                Header = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.SessionHeaderEntry);
            }
            catch (JsonException)
            {
                // Not a header after all (a crafted file can carry the marker
                // in a record that is not one). The caller reports a
                // header-less file; the history under it still parses.
            }
        }

        /// <summary>
        ///     The parsed messages in chronological order: arrival order when
        ///     the log is append-linear (the common case, returned as-is), a
        ///     stable <c>CreatedAt</c> sort when the file was not written in
        ///     time order.
        /// </summary>
        internal IReadOnlyList<AgentMessage> ToChronological()
        {
            int count = _arrival.Count;
            for (int i = 1; i < count; i++)
            {
                if (_arrival[i - 1].CreatedAt > _arrival[i].CreatedAt)
                {
                    // Same stable sort the read path has always applied, run
                    // only once — on the read that detects disorder.
                    return _arrival.OrderBy(m => m.CreatedAt).ToList();
                }
            }

            return _arrival;
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

/// <summary>
///     SG8: BCL <c>[LoggerMessage]</c> delegates for <see cref="SessionFileReader" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SessionFileReaderLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}")]
    public static partial void MalformedLinesEncountered(ILogger logger, int errorCount, string sessionId, string errors);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Session {SessionId} holds a record over the {MaxBytes} byte ceiling; it was skipped and the rest of the session was read.")]
    public static partial void OversizedRecordSkipped(ILogger logger, string sessionId, long maxBytes);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Session {SessionId} was being appended to while it was read; returning {MessageCount} message(s) from a partial snapshot (not cached).")]
    public static partial void PartialSnapshotReturned(ILogger logger, string sessionId, int messageCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Session {SessionId} ends with a record that could not be decoded: the write was cut at the tail. The rest of the session was read; the interrupted record is reported as unreadable.")]
    public static partial void TruncatedTailSkipped(ILogger logger, string sessionId);
}
