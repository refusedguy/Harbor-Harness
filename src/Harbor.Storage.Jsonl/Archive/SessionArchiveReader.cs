// SessionArchiveReader.cs — read a session archive back (#1750).
//
// WHY A READER AT ALL
// -------------------
// The export is one command; the reader is what makes the FORMAT a promise
// instead of a convention. An archive nobody can read back is a blob with a
// file extension, and a format whose round-trip is never checked is a format
// that drifts. The reader is therefore part of the feature, not a convenience
// for a future import verb: the round-trip test reads an archive this build
// wrote and asserts that the session comes back whole.
//
// WHAT COMES BACK
// ---------------
// The manifest, the session record, and the log — every record the archive
// holds, shadowed ones included, with the fold's verdict on each. The
// attachments are resolved back into the messages that referenced them, so a
// message that carried an image carries the image's BYTES again, not a
// reference to a file inside a ZIP.
//
// WHAT IT REFUSES
// ----------------
// An archive whose manifest does not carry the marker is not a Harbor
// archive. An archive whose version this build does not know is not readable
// — guessing at the lines under it would produce a session that looks fine
// and is not. An archive that names an attachment it does not hold, or holds
// one whose bytes do not hash to the digest it claims, is corrupt: both are
// failures, because the alternative is a session that quietly lost an image.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     A session archive, read back: the manifest, the session record and the
///     whole log (#1750).
/// </summary>
/// <param name="Manifest">The manifest the archive carries.</param>
/// <param name="Session">The session record the log belongs to.</param>
/// <param name="Log">
///     The log, in write order, every record the archive holds — including the
///     ones the fold calls shadowed.
/// </param>
public sealed record SessionArchive(
    SessionArchiveManifest Manifest,
    Session Session,
    IReadOnlyList<SessionLogEntry> Log);

/// <summary>
///     Reads a session archive written by <see cref="SessionArchiveWriter" />
///     (#1750).
/// </summary>
public sealed class SessionArchiveReader
{
    /// <summary>
    ///     Read an archive.
    /// </summary>
    /// <param name="input">
    ///     The stream holding the ZIP. A stream that cannot seek is read
    ///     into memory first: the ZIP central directory is read from the
    ///     end of the file, so a seekable stream is what the format needs.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The archive, or failure with a diagnostic.
    /// </returns>
    public async Task<Result<SessionArchive>> ReadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var attempted = await Result.Try(
                async () =>
                {
                    Stream source = input;
                    MemoryStream? buffer = null;
                    if (!input.CanSeek)
                    {
                        buffer = new MemoryStream();
                        await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
                        buffer.Position = 0;
                        source = buffer;
                    }

                    try
                    {
                        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
                        return await ReadArchiveAsync(archive, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        buffer?.Dispose();
                    }
                },
                ReadFailure)
            .ConfigureAwait(false);

        // The lambda's own Result is the T here (Result.Try has no
        // Func<Result<T>> overload), so the rail is joined by hand — the
        // same shape SessionPorter.DecodeMessageLine uses.
        return attempted.Bind(x => x);
    }

    /// <summary>
    ///     The diagnostic a failed read carries. Cancellation is not a failed
    ///     read: it propagates as itself, the way every other store call in
    ///     this tree does.
    /// </summary>
    private static string ReadFailure(Exception ex) =>
        ex is OperationCanceledException
            ? throw ex
            : $"Cannot read the session archive: {ex.Message}";

    /// <summary>
    ///     Read the three parts of an archive.
    /// </summary>
    /// <remarks>
    ///     Three links, so a chain and not a ladder: docs/ROP-API-INVENTORY.md
    ///     §3.1 puts the ladder at one or two links and the chain at three up.
    ///     The manifest, the session and the log each travel through the rail
    ///     instead of being unwrapped beside a guard.
    /// </remarks>
    private static async Task<Result<SessionArchive>> ReadArchiveAsync(ZipArchive archive, CancellationToken ct)
    {
        return await ReadManifest(archive)
            .Bind(async manifest => (await ReadSessionLineAsync(archive, ct).ConfigureAwait(false))
                .Bind(async session => (await ReadLogAsync(archive, session.Id, ct).ConfigureAwait(false))
                    .Map(log => new SessionArchive(manifest, session, log))))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Read and check the manifest.
    /// </summary>
    private static Result<SessionArchiveManifest> ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(SessionArchiveFormat.ManifestEntryName);
        if (entry is null)
        {
            return Result.Failure<SessionArchiveManifest>(
                $"Not a Harbor session archive: no '{SessionArchiveFormat.ManifestEntryName}' entry.");
        }

        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize(stream, JsonlCodecContext.Default.SessionArchiveManifest);
        if (manifest is null)
        {
            return Result.Failure<SessionArchiveManifest>("The archive's manifest is empty.");
        }

        if (manifest.Marker != SessionArchiveFormat.Marker)
        {
            return Result.Failure<SessionArchiveManifest>(
                $"Not a Harbor session archive (missing '{SessionArchiveFormat.Marker}' marker).");
        }

        if (manifest.Version > SessionArchiveFormat.Version)
        {
            return Result.Failure<SessionArchiveManifest>(
                $"Unsupported archive format version {manifest.Version} (this build reads up to {SessionArchiveFormat.Version}).");
        }

        return Result.Success(manifest);
    }

    /// <summary>
    ///     Read the log's header line: the session record.
    /// </summary>
    private static async Task<Result<Session>> ReadSessionLineAsync(ZipArchive archive, CancellationToken ct)
    {
        var entry = archive.GetEntry(SessionArchiveFormat.LogEntryName);
        if (entry is null)
        {
            return Result.Failure<Session>(
                $"Not a Harbor session archive: no '{SessionArchiveFormat.LogEntryName}' entry.");
        }

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return Result.Failure<Session>("The archive's log is empty.");
        }

        var header = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.ArchiveSessionLine);
        if (header is null)
        {
            return Result.Failure<Session>("The archive's log header is unreadable.");
        }

        return Result.Success(header.Session);
    }

    /// <summary>
    ///     Read the log: one record per line, in write order.
    /// </summary>
    private static async Task<Result<IReadOnlyList<SessionLogEntry>>> ReadLogAsync(
        ZipArchive archive,
        string sessionId,
        CancellationToken ct)
    {
        var entry = archive.GetEntry(SessionArchiveFormat.LogEntryName);
        if (entry is null)
        {
            return Result.Failure<IReadOnlyList<SessionLogEntry>>(
                $"Not a Harbor session archive: no '{SessionArchiveFormat.LogEntryName}' entry.");
        }

        var records = new List<SessionLogEntry>();
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);

        bool first = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            // The header line was already read; the records start after it.
            if (first)
            {
                first = false;
                continue;
            }

            // The decode of one line is a Result, and the first failure
            // stops the fold. The decode rides the chain the reader's path
            // is built on (three links), so it does not unwrap beside a
            // guard.
            var decoded = ReadRecord(sessionId, line, archive).Map(entry =>
            {
                records.Add(entry);
                return entry;
            });
            if (decoded.IsFailure)
                return decoded.ConvertFailure<IReadOnlyList<SessionLogEntry>>();
        }

        return Result.Success<IReadOnlyList<SessionLogEntry>>(records);
    }

    /// <summary>
    ///     Read one record of the log.
    /// </summary>
    private static Result<SessionLogEntry> ReadRecord(string sessionId, string line, ZipArchive archive)
    {
        return Result.Try(
                () =>
                {
                    using var document = JsonDocument.Parse(line);
                    JsonElement element = document.RootElement;
                    string type = element.TryGetProperty("type", out JsonElement typeElement)
                        ? typeElement.GetString() ?? string.Empty
                        : string.Empty;

                    switch (type)
                    {
                        case SessionArchiveRecordTypes.Message:
                        {
                            var messageLine = element.Deserialize(JsonlCodecContext.Default.ArchiveMessageLine);
                            if (messageLine is null)
                            {
                                throw new InvalidDataException("the message record is unreadable.");
                            }

                            AgentMessage message = ReadMessage(sessionId, element);
                            if (message is UserMessage user)
                            {
                                message = user with { Attachments = ResolveAttachments(element, archive) };
                            }

                            return new SessionLogEntry
                            {
                                Kind = SessionLogEntryKind.Message,
                                Id = messageLine.Id,
                                Position = messageLine.Position,
                                Message = message,
                                State = SessionArchiveStates.State(messageLine.State),
                            };
                        }

                        case SessionArchiveRecordTypes.Rewind:
                        {
                            var rewindLine = element.Deserialize(JsonlCodecContext.Default.ArchiveRewindLine);
                            if (rewindLine is null)
                            {
                                throw new InvalidDataException("the rewind record is unreadable.");
                            }

                            return new SessionLogEntry
                            {
                                Kind = SessionLogEntryKind.Rewind,
                                Id = rewindLine.Id,
                                Position = rewindLine.Position,
                                AnchorId = rewindLine.AnchorId,
                                OccurredAt = rewindLine.OccurredAt,
                                RemovedCount = rewindLine.RemovedCount,
                                State = SessionArchiveStates.State(rewindLine.State),
                            };
                        }

                        default:
                            throw new InvalidDataException($"unknown record type '{type}'.");
                    }
                },
                ex => $"malformed archive record: {ex.Message}");
    }

    /// <summary>
    ///     Read the message a message record carries. The archive's user
    ///     payload differs from the session file's only in its attachments,
    ///     so the session codec decodes the rest of it unchanged.
    /// </summary>
    private static AgentMessage ReadMessage(string sessionId, JsonElement element)
    {
        var decoded = JsonlMessageCodec.DeserializeMessage(sessionId, element);
        if (decoded.IsFailure)
        {
            throw new InvalidDataException(decoded.Error);
        }

        return decoded.Value;
    }

    /// <summary>
    ///     Resolve the attachments a message record references, back into the
    ///     bytes they stand for.
    /// </summary>
    private static IReadOnlyList<ImageAttachment>? ResolveAttachments(JsonElement element, ZipArchive archive)
    {
        if (!element.TryGetProperty("payload", out JsonElement payload)
            || !payload.TryGetProperty("attachments", out JsonElement array)
            || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() == 0)
        {
            return null;
        }

        var attachments = new List<ImageAttachment>(array.GetArrayLength());
        foreach (JsonElement reference in array.EnumerateArray())
        {
            string digest = reference.TryGetProperty("sha256", out JsonElement sha) ? sha.GetString() ?? string.Empty : string.Empty;
            string path = reference.TryGetProperty("ref", out JsonElement pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty;
            if (digest.Length == 0 || path.Length == 0)
            {
                throw new InvalidDataException("an attachment reference names neither a digest nor a path.");
            }

            byte[] bytes = ReadAttachment(archive, path);

            // The archive names the bytes by their digest, so a mismatch is a
            // corrupt archive — not a detail to shrug at.
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"the attachment '{path}' hashes to '{actual}', not the '{digest}' the archive claims.");
            }

            string original = reference.TryGetProperty("path", out JsonElement originalElement)
                ? originalElement.GetString() ?? string.Empty
                : string.Empty;
            string mimeType = reference.TryGetProperty("mimeType", out JsonElement mimeElement)
                ? mimeElement.GetString() ?? string.Empty
                : string.Empty;
            int width = reference.TryGetProperty("width", out JsonElement widthElement) && widthElement.TryGetInt32(out int w) ? w : 0;
            int height = reference.TryGetProperty("height", out JsonElement heightElement) && heightElement.TryGetInt32(out int h) ? h : 0;

            attachments.Add(new ImageAttachment(original, mimeType, width, height, bytes));
        }

        return attachments.Count == 0 ? null : attachments;
    }

    /// <summary>
    ///     Read the bytes of one attachment out of the ZIP.
    /// </summary>
    private static byte[] ReadAttachment(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            throw new InvalidDataException($"the archive names the attachment '{path}' and does not hold it.");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
