// SessionArchiveWriter.cs — one session, one ZIP (#1750).
//
// WHAT THIS WRITES
// ----------------
// A session archive is three things and nothing else:
//
//   manifest.json              the format marker, the version, the counts,
//                              the index of the attachments
//   session.jsonl              the canonical log: a header line and one line
//                              per record, in write order
//   attachments/<sha256>.<ext> the attachment bytes, content-addressed
//
// The log is the WHOLE log, not the surface: the records a rewind shadowed
// and the records an edit replaced are in the archive, because the point of
// an archive is to say what happened, and a rewind that erased its own tail
// would be a log that cannot answer the question it was collected for. When
// the store has no append-only log yet (#1784 — the model exists, the
// backends have not been migrated), the archive holds the surface and SAYS
// SO in the manifest: `logSource` is `log` or `surface`, and a reader is
// never left to infer which one it is holding.
//
// WHERE THE DATA COMES FROM
// -------------------------
// The archive is assembled from the STORE, through `ISessionStore` — never
// from the files the store happens to keep. That is what makes the export
// backend-agnostic: the SQLite backend has no file to copy, and the JSONL
// backend's file is an implementation detail of that backend, not the
// session.
//
// SECRETS
// -------
// The archive is the one artifact that is MEANT to leave the machine — it is
// what a user attaches to a bug report. So the archive never carries a
// credential, whatever tier the log on disk was written in:
//
//   * in the keyless tier (#1740) the log on disk is already scrubbed, and
//     the scrub here finds nothing left to remove;
//   * in the local tier the log on disk is NOT scrubbed, and this is where
//     the credential goes.
//
// Both passes use the SAME scrubber (`SecretRedactor`), so the placeholders
// in an archive are the ones a keyless log already uses and the two artifacts
// read alike. The scrub is idempotent, which is what lets the two passes
// overlap without the second mangling the first.
//
// The scrub covers every text-bearing field — the session title, the message
// content, the thinking, the tool-call arguments and the tool output —
// because a credential the agent read out of a .env lands in a TOOL RESULT
// and a credential the user pasted lands in the PROMPT. It does not touch
// the provider id, the model id or the paths: those name what happened, not
// a secret, and a log that does not say which model produced it cannot be
// replayed against anything.
//
// MEMORY
// ------
// The ZIP is written entry by entry into the caller's stream, so the archive
// is never assembled in one buffer. The log's lines are produced one at a
// time; the attachment bytes are written from the arrays the session already
// holds, so the writer adds no copy of them.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Writes one session as a ZIP archive: the canonical log, the
///     attachments, and the manifest that says which format wrote them
///     (#1750).
/// </summary>
public sealed class SessionArchiveWriter
{
    /// <summary>
    ///     The newline that separates two records of the archive's log.
    /// </summary>
    private static readonly byte[] LineFeed = [(byte)'\n'];

    private readonly SecretRedactor _redactor;
    private readonly ILogger<SessionArchiveWriter> _logger;

    /// <summary>
    ///     Construct a writer.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="literalSecrets">
    ///     The credential values the process holds. Occurrences of each are
    ///     replaced; without them the shape rules still apply, but a
    ///     credential with an unrecognisable shape would survive.
    /// </param>
    public SessionArchiveWriter(ILogger<SessionArchiveWriter> logger, IReadOnlyCollection<string>? literalSecrets = null)
    {
        _logger = logger;
        _redactor = new SecretRedactor(literalSecrets);
    }

    /// <summary>
    ///     Write one session as a ZIP archive.
    /// </summary>
    /// <param name="store">The store to read the session from.</param>
    /// <param name="sessionId">The session to export.</param>
    /// <param name="output">
    ///     The stream to write the archive into. The caller owns its
    ///     lifetime; the archive is closed when this method returns.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The manifest that was written, or failure with a diagnostic.
    /// </returns>
    public async Task<Result<SessionArchiveManifest>> WriteAsync(
        ISessionStore store,
        string sessionId,
        Stream output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(output);

        var sessionResult = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure) // §4.6-ok: propagate the store diagnostic verbatim.
            return sessionResult.ConvertFailure<SessionArchiveManifest>()
                .MapError(e => $"Cannot export session '{sessionId}': {e}");

        // The session, the log and the archive itself travel through one rail:
        // the archive is a pipeline of three reads, and a ladder of three
        // guards beside a hundred lines of assembly reads worse than the
        // chain (docs/ROP-API-INVENTORY.md §3.1).
        return await sessionResult
            .Bind(async session => (await ReadLogAsync(store, session.Id, ct).ConfigureAwait(false))
                .Bind(log => WriteArchiveAsync(store, session, log, output, ct)))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Build the manifest and write the archive: the fold, the scrub, the
    ///     attachments and the three entries of the ZIP.
    /// </summary>
    private async Task<Result<SessionArchiveManifest>> WriteArchiveAsync(
        ISessionStore store,
        Session session,
        ArchiveLog log,
        Stream output,
        CancellationToken ct)
    {
        SessionArchiveIntegrity? integrity = await ReadIntegrityAsync(store, session.Id, ct).ConfigureAwait(false);

        // The fold decides what is current and what is shadowed. It is a
        // property of the log as a whole, so it is done once, here, and the
        // verdict travels with every record into the archive.
        SessionSurface surface = SessionLogFold.Fold(log.Entries);

        // The scrub happens on the way OUT of the store: the archive is the
        // artifact that leaves the machine, and it must not carry a
        // credential whether or not the log on disk does.
        var scrubbed = new List<SessionLogEntry>(surface.Entries.Count);
        foreach (SessionLogEntry entry in surface.Entries)
        {
            AgentMessage? message = entry.Message;
            scrubbed.Add(message is null ? entry : entry with { Message = _redactor.Scrub(message) });
        }

        Session scrubbedSession = session with { Title = _redactor.ScrubText(session.Title) };

        var attachments = new List<SessionArchiveAttachment>();
        var attachmentBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var refsByPosition = new Dictionary<int, ArchiveAttachmentRef[]>();
        CollectAttachments(scrubbed, attachments, attachmentBytes, refsByPosition);

        var manifest = new SessionArchiveManifest(
            Marker: SessionArchiveFormat.Marker,
            Version: SessionArchiveFormat.Version,
            CreatedAt: DateTimeOffset.UtcNow,
            SessionId: session.Id,
            LogSource: log.Source,
            LogEntries: scrubbed.Count,
            CurrentEntries: surface.Current.Count,
            ShadowedEntries: surface.Shadowed.Count,
            LogOnlyEntries: surface.LogOnly.Count,
            Integrity: integrity,
            Attachments: [.. attachments]);

        try
        {
            await WriteZipAsync(manifest, scrubbedSession, scrubbed, refsByPosition, attachments, attachmentBytes, output, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Cancellation is NOT one of these, so it propagates as itself
            // rather than as a failed export.
            return Result.Failure<SessionArchiveManifest>(
                $"Cannot write the archive of session '{session.Id}': {ex.Message}");
        }

        SessionArchiveWriterLog.ArchiveWritten(_logger, session.Id, manifest.LogEntries, attachments.Count);
        return Result.Success(manifest);
    }

    /// <summary>
    ///     Write the three parts of the archive into the ZIP.
    /// </summary>
    private static async Task WriteZipAsync(
        SessionArchiveManifest manifest,
        Session session,
        IReadOnlyList<SessionLogEntry> entries,
        IReadOnlyDictionary<int, ArchiveAttachmentRef[]> refsByPosition,
        IReadOnlyList<SessionArchiveAttachment> attachments,
        IReadOnlyDictionary<string, byte[]> attachmentBytes,
        Stream output,
        CancellationToken ct)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // The manifest first: it is the file a reader opens to learn whether
        // it can read this archive at all.
        var manifestEntry = archive.CreateEntry(SessionArchiveFormat.ManifestEntryName, CompressionLevel.Optimal);
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                    manifestStream,
                    manifest,
                    JsonlCodecContext.Default.SessionArchiveManifest,
                    ct)
                .ConfigureAwait(false);
        }

        var logEntry = archive.CreateEntry(SessionArchiveFormat.LogEntryName, CompressionLevel.Optimal);
        await using (var logStream = logEntry.Open())
        {
            var header = new ArchiveSessionLine(
                Type: SessionArchiveRecordTypes.Session,
                Version: SessionArchiveFormat.Version,
                Session: session);
            await WriteLineAsync(
                    logStream,
                    JsonSerializer.SerializeToUtf8Bytes(header, JsonlCodecContext.Default.ArchiveSessionLine),
                    ct)
                .ConfigureAwait(false);

            foreach (SessionLogEntry entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Kind == SessionLogEntryKind.Rewind)
                {
                    var rewind = new ArchiveRewindLine(
                        Type: SessionArchiveRecordTypes.Rewind,
                        Id: entry.Id,
                        Position: entry.Position,
                        State: SessionArchiveStates.Name(entry.State),
                        AnchorId: entry.AnchorId,
                        OccurredAt: entry.OccurredAt,
                        RemovedCount: entry.RemovedCount);
                    await WriteLineAsync(
                            logStream,
                            JsonSerializer.SerializeToUtf8Bytes(rewind, JsonlCodecContext.Default.ArchiveRewindLine),
                            ct)
                        .ConfigureAwait(false);
                    continue;
                }

                // A message record with no message is in the log but not in
                // the surface (the fold says so in its warnings). There is
                // nothing to write for it, and a line with an empty payload
                // would be a message that never was.
                AgentMessage? message = entry.Message;
                if (message is null)
                {
                    continue;
                }

                refsByPosition.TryGetValue(entry.Position, out ArchiveAttachmentRef[]? refs);
                var line = new ArchiveMessageLine(
                    Type: SessionArchiveRecordTypes.Message,
                    Id: entry.Id,
                    Position: entry.Position,
                    State: SessionArchiveStates.Name(entry.State),
                    CreatedAt: message.CreatedAt,
                    ParentId: message.ParentId,
                    Role: message.Role,
                    Payload: JsonlMessageCodec.SerializeArchiveMessagePayload(message, refs));
                await WriteLineAsync(
                        logStream,
                        JsonSerializer.SerializeToUtf8Bytes(line, JsonlCodecContext.Default.ArchiveMessageLine),
                        ct)
                    .ConfigureAwait(false);
            }
        }

        foreach (SessionArchiveAttachment attachment in attachments)
        {
            ct.ThrowIfCancellationRequested();
            if (!attachmentBytes.TryGetValue(attachment.Sha256, out byte[]? bytes))
            {
                continue;
            }

            var entry = archive.CreateEntry(attachment.Ref, CompressionLevel.Optimal);
            await using var attachmentStream = entry.Open();
            await attachmentStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Write one JSON line: the bytes and the newline that ends them.
    /// </summary>
    private static async Task WriteLineAsync(Stream stream, byte[] json, CancellationToken ct)
    {
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.WriteAsync(LineFeed, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Collect the attachments of the log, content-addressed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The KEY is the digest of the bytes, so the same image attached
    ///         to three turns is one file in the ZIP and three references to
    ///         it. The extension comes from the MIME type of the FIRST
    ///         occurrence, so two attachments with the same bytes and two
    ///         different MIME types still land in ONE file — the second one
    ///         points at the first one's file.
    ///     </para>
    /// </remarks>
    private static void CollectAttachments(
        IReadOnlyList<SessionLogEntry> entries,
        List<SessionArchiveAttachment> attachments,
        Dictionary<string, byte[]> attachmentBytes,
        Dictionary<int, ArchiveAttachmentRef[]> refsByPosition)
    {
        var known = new Dictionary<string, SessionArchiveAttachment>(StringComparer.Ordinal);

        foreach (SessionLogEntry entry in entries)
        {
            if (entry.Message is not UserMessage user || user.Attachments is not { Count: > 0 })
            {
                continue;
            }

            var refs = new ArchiveAttachmentRef[user.Attachments.Count];
            for (int i = 0; i < user.Attachments.Count; i++)
            {
                ImageAttachment image = user.Attachments[i];
                string digest = Digest(image.Data);
                if (!known.TryGetValue(digest, out SessionArchiveAttachment? stored))
                {
                    stored = new SessionArchiveAttachment(
                        Sha256: digest,
                        Ref: AttachmentRef(digest, image.MimeType),
                        SizeBytes: image.Data.LongLength,
                        MimeType: image.MimeType,
                        Path: image.Path,
                        Width: image.Width,
                        Height: image.Height);
                    known[digest] = stored;
                    attachments.Add(stored);
                    attachmentBytes[digest] = image.Data;
                }

                refs[i] = new ArchiveAttachmentRef(
                    Path: image.Path,
                    MimeType: image.MimeType,
                    Width: image.Width,
                    Height: image.Height,
                    Sha256: digest,
                    Ref: stored.Ref,
                    SizeBytes: image.Data.LongLength);
            }

            refsByPosition[entry.Position] = refs;
        }
    }

    /// <summary>
    ///     The SHA-256 of some bytes, lower-case hex.
    /// </summary>
    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    ///     Where an attachment's bytes live inside the ZIP.
    /// </summary>
    private static string AttachmentRef(string digest, string mimeType) =>
        $"{SessionArchiveFormat.AttachmentsPrefix}{digest}{ExtensionFor(mimeType)}";

    /// <summary>
    ///     The file extension an attachment's MIME type maps to. An
    ///     unrecognised type gets <c>.bin</c>: the bytes are still in the
    ///     archive, and the manifest still says what they are.
    /// </summary>
    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/tiff" => ".tiff",
        "image/avif" => ".avif",
        _ => ".bin",
    };

    /// <summary>
    ///     Read the session's log, or its surface when the store has no
    ///     append-only log yet (#1784).
    /// </summary>
    private static async Task<Result<ArchiveLog>> ReadLogAsync(
        ISessionStore store,
        string sessionId,
        CancellationToken ct)
    {
        var logResult = await store.GetSessionLogAsync(sessionId, ct).ConfigureAwait(false);
        if (logResult.IsSuccess)
        {
            IReadOnlyList<SessionLogEntry>? entries = logResult.Value;
            return Result.Success(new ArchiveLog(entries ?? [], SessionArchiveFormat.LogSourceLog));
        }

        // The store does not expose a log: the surface is what it can offer,
        // and the manifest says which of the two the archive holds.
        var messages = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messages.IsFailure) // §4.6-ok: propagate the store diagnostic verbatim.
            return messages.ConvertFailure<ArchiveLog>()
                .MapError(e => $"Cannot export messages of session '{sessionId}': {e}");

        IReadOnlyList<AgentMessage>? list = messages.Value;
        var synthesized = new List<SessionLogEntry>(list?.Count ?? 0);
        if (list is not null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                AgentMessage message = list[i];
                synthesized.Add(new SessionLogEntry
                {
                    Kind = SessionLogEntryKind.Message,
                    Id = message.Id,
                    Position = i,
                    Message = message,
                });
            }
        }

        return Result.Success(new ArchiveLog(synthesized, SessionArchiveFormat.LogSourceSurface));
    }

    /// <summary>
    ///     Read what the store's transcript read could not deliver, when the
    ///     store exposes a transcript. A store that does not has nothing to
    ///     say, and an absent field says "nobody checked" where a zero would
    ///     say "nothing was missing".
    /// </summary>
    private static async Task<SessionArchiveIntegrity?> ReadIntegrityAsync(
        ISessionStore store,
        string sessionId,
        CancellationToken ct)
    {
        var transcript = await store.GetTranscriptAsync(sessionId, ct).ConfigureAwait(false);
        if (transcript.IsFailure)
        {
            return null;
        }

        SessionTranscript? read = transcript.Value;
        if (read is null)
        {
            return null;
        }

        TranscriptIntegrity integrity = read.Integrity;
        return new SessionArchiveIntegrity(
            UnreadableRecords: integrity.UnreadableRecordCount,
            IsTailTruncated: integrity.IsTailTruncated,
            UnclosedToolCalls: integrity.UnclosedToolCalls.Count);
    }

    /// <summary>
    ///     The log the archive holds, and where it came from.
    /// </summary>
    private sealed record ArchiveLog(IReadOnlyList<SessionLogEntry> Entries, string Source);
}

/// <summary>
///     SG: BCL <c>[LoggerMessage]</c> delegates for <see cref="SessionArchiveWriter" />.
/// </summary>
internal static partial class SessionArchiveWriterLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Wrote the archive of session {SessionId}: {Entries} record(s), {Attachments} attachment(s)")]
    public static partial void ArchiveWritten(ILogger logger, string sessionId, int entries, int attachments);
}
