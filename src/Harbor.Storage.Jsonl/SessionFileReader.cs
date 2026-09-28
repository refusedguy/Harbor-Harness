// SessionFileReader.cs — JSONL session-file read path (header + messages).
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). The store owns the cache + locks; this file owns bytes →
// records: header decode, full-file message parse, and the line classifiers
// the rewrite paths reuse.

using System.Buffers;
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
    ///     True when the line is a <c>"message"</c> entry carrying the given id.
    ///     Header lines and unparseable lines are never matched, so a rewrite
    ///     cannot accidentally drop them. Malformed lines are left untouched on
    ///     disk — the read path already reports them as parse warnings.
    /// </summary>
    internal static bool IsMessageEntryWithId(string line, string messageId)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains($"\"id\":\"{messageId}\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var entry = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.MessageEntry);
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

    /// <summary>True when the line is a <c>"message"</c> entry with any id.</summary>
    internal static bool IsAnyMessageEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"type\":\"message\"", StringComparison.Ordinal))
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
    ///     errors are aggregated into a <c>List&lt;string&gt;</c> and surfaced
    ///     via <see cref="ILogger.LogWarning" />, while still returning the
    ///     successfully deserialized messages (§ROP-001 resolved).
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
    ///         length measured on the <em>open handle</em> — never by the pooled
    ///         buffer's capacity and never by a stat taken before the open. The
    ///         old shape measured a <see cref="FileInfo" /> length, then read
    ///         <c>while (read &lt; buffer.Length)</c>; since the pool returns an
    ///         array <em>larger</em> than requested, an append landing in that
    ///         window extended the read past the measured end and cut the new
    ///         record mid-line. The truncated line was then merely
    ///         <c>LogWarning</c>ed, and the short list was published to the
    ///         store's cache as if it were the whole session.
    ///     </para>
    /// </remarks>
    /// <param name="sessionFile">Absolute path to the .jsonl file.</param>
    /// <param name="sessionId">The session id (passed through to the parser).</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    /// <param name="ct">Cancellation token observed by the file read.</param>
    /// <returns>
    ///     The chronological message list plus its provenance, or failure when
    ///     the file is too large to read into a single buffer.
    /// </returns>
    internal static async Task<Result<MessageReadSnapshot>> ParseMessagesFromDiskAsync(
        string sessionFile,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        var messages = new Dictionary<string, AgentMessage>();
        var errors = new List<string>(capacity: 0);

        // Taken BEFORE the open: paired with the post-read stat it proves the
        // bytes we parsed are exactly the file state this stat names.
        var stat = SessionFileStat.Read(sessionFile);
        if (stat.Length > int.MaxValue)
            return TooLarge<MessageReadSnapshot>(stat.Length);

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(stat.Length, 1));
        try
        {
            int read = 0;
            bool stable = false;

            // A single concurrent append is the common case and the retry
            // resolves it: the next attempt measures the grown file and reads
            // it whole. The bound keeps a writer streaming faster than we read
            // from turning into an unbounded spin — the last attempt is then
            // returned as an explicitly unstable snapshot instead.
            for (int attempt = 1; attempt <= MaxSnapshotAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Re-measure every attempt; the previous one may be stale.
                stat = SessionFileStat.Read(sessionFile);
                if (stat.Length > int.MaxValue)
                    return TooLarge<MessageReadSnapshot>(stat.Length);
                buffer = EnsureCapacity(buffer, stat.Length);

                long budget;
                using (var fs = new FileStream(
                           sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                           bufferSize: 64 * 1024, useAsync: true))
                {
                    // Budget from the OPEN handle, not from a pre-open stat.
                    budget = fs.Length;
                    if (budget > int.MaxValue)
                        return TooLarge<MessageReadSnapshot>(budget);
                    buffer = EnsureCapacity(buffer, budget);

                    // Bounded by the measured length — NOT by buffer.Length,
                    // which the pool rounds up and which is therefore larger
                    // than the file. That overshoot is the truncation (#459).
                    read = 0;
                    while (read < budget)
                    {
                        int n = await fs.ReadAsync(buffer.AsMemory(read, (int)(budget - read)), ct).ConfigureAwait(false);
                        if (n == 0) break;
                        read += n;
                    }
                }

                // Unchanged mtime across the read, unchanged length, and every
                // measured byte actually read == the parsed content is the whole
                // file for that stat. An append moves the length; an atomic
                // rewrite (temp + rename) moves the path's mtime.
                var after = SessionFileStat.Read(sessionFile);
                stable = read == budget && after.Matches(stat) && after.Length == budget;
                if (stable)
                    break;
            }

            ParseLines(buffer.AsSpan(0, read), sessionId, messages, errors);

            if (errors.Count > 0)
            {
                logger.LogWarning("Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}",
                    errors.Count, sessionId, string.Join("; ", errors));
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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Rounds a pooled buffer up to at least <paramref name="required" />
    ///     bytes, returning the old one to the pool when it had to grow. The
    ///     caller keeps ownership of whichever buffer comes back.
    /// </summary>
    private static byte[] EnsureCapacity(byte[] buffer, long required)
    {
        if (required <= buffer.Length)
            return buffer;

        // Rent before returning, so a throw from the pool cannot leave the
        // caller's finally returning the same array twice.
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Max(required, 1));
        ArrayPool<byte>.Shared.Return(buffer);
        return grown;
    }

    private static Result<T> TooLarge<T>(long length) =>
        Result.Failure<T>($"Session file too large ({length} bytes); refusing unbounded read.");

    /// <summary>
    ///     Split <paramref name="content" /> on LF and fold every parseable
    ///     message entry into <paramref name="messages" /> (latest id wins);
    ///     header lines, blank lines and per-line parse failures are routed to
    ///     <paramref name="errors" /> or dropped.
    /// </summary>
    private static void ParseLines(
        ReadOnlySpan<byte> content,
        string sessionId,
        Dictionary<string, AgentMessage> messages,
        List<string> errors)
    {
        ReadOnlySpan<byte> rest = content;
        if (rest.StartsWith("\xEF\xBB\xBF"u8))
        {
            rest = rest[3..];
        }

        while (!rest.IsEmpty)
        {
            int nl = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = nl < 0 ? rest : rest[..nl];
            rest = nl < 0 ? default : rest[(nl + 1)..];

            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty || line.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
            {
                continue;
            }

            if (IsSessionHeaderLine(line))
            {
                continue;
            }

            var msgResult = JsonlLineParser.Parse(line, sessionId);
            if (msgResult.IsSuccess)
            {
                messages[msgResult.Value.Id] = msgResult.Value;
            }
            else
            {
                errors.Add($"Line parse failed: {msgResult.Error}");
            }
        }
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
        string? firstLine;
        try
        {
            using var reader = new StreamReader(path);
            firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Failure<SessionHeaderEntry>(
                $"Session '{sessionId}' header unreadable ({ResultErrors.Message(ex)}): {path}.");
        }

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
