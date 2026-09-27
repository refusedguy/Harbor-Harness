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
    ///     list. Per-line JSON parse errors are aggregated into a
    ///     <c>List&lt;string&gt;</c> and surfaced via <see cref="ILogger.LogWarning" />,
    ///     while still returning the successfully deserialized messages
    ///     (§ROP-001 resolved).
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
    /// </remarks>
    /// <param name="sessionFile">Absolute path to the .jsonl file.</param>
    /// <param name="sessionId">The session id (passed through to the parser).</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    /// <param name="ct">Cancellation token observed by the file read.</param>
    /// <returns>The chronological message list, or failure with the first error.</returns>
    internal static async Task<Result<IReadOnlyList<AgentMessage>>> ParseMessagesFromDiskAsync(
        string sessionFile,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        var messages = new Dictionary<string, AgentMessage>();
        var errors = new List<string>(capacity: 0);

        long fileLength = new FileInfo(sessionFile).Length;
        if (fileLength > int.MaxValue)
            return Result.Failure<IReadOnlyList<AgentMessage>>(
                $"Session file too large ({fileLength} bytes); refusing unbounded read.");
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(fileLength, 1));
        try
        {
            int read = 0;
            using (var fs = new FileStream(
                       sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 64 * 1024, useAsync: true))
            {
                while (read < buffer.Length)
                {
                    int n = await fs.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }

            ReadOnlySpan<byte> rest = buffer.AsSpan(0, read);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (errors.Count > 0)
        {
            logger.LogWarning("Encountered {ErrorCount} malformed line(s) reading session {SessionId}: {Errors}",
                errors.Count, sessionId, string.Join("; ", errors));
        }

        var ordered = messages.Values.OrderBy(m => m.CreatedAt).ToList();
        return Result.Success<IReadOnlyList<AgentMessage>>(ordered);
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
