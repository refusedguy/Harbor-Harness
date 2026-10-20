using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Portable session export/import for the JSONL codec family (V4-slice
///     "session polish"). Line 1: <see cref="ExportEnvelope" /> with the full
///     <see cref="Session" /> record; lines 2..n: one <see cref="MessageEntry" />
///     per persisted message in chronological order.
/// </summary>
/// <remarks>
///     <para>
///         Serialization rides the SAME AOT-safe <see cref="JsonlCodecContext" /> used by
///         <c>JsonlSessionStore</c> — no second encoding of messages exists, so an export
///         is byte-compatible with what the store itself persists and import decodes it
///         through the identical, diagnostics-preserving <see cref="JsonlMessageCodec"/> railway.
///     </para>
///     <para>
///         Import is append-only by contract (<see cref="ISessionPorter" /> remarks): a fresh
///         id is minted on every import so running it twice yields two independent copies.
///     </para>
///     <para>
///         <b>Export is a snapshot, not a stream (<see cref="ExportAsync" />, #1785).</b>
///         The whole export is assembled from one read of the session, so the stats it
///         carries always describe the history it carries.
///     </para>
/// </remarks>
public sealed class JsonlSessionPorter : ISessionPorter
{
    /// <summary>Bump on any envelope-shape change; ImportAsync rejects newer majors.</summary>
    internal const int SchemaVersion = 1;

    private static readonly string EnvelopeMarker = "$harbor-session-export";

    private readonly ILogger<JsonlSessionPorter> _logger;

    public JsonlSessionPorter(ILogger<JsonlSessionPorter> logger) => _logger = logger;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>#1785 — the export is a SNAPSHOT, taken at one instant.</b> The
    ///         envelope (the session record + the stats) and the body (the
    ///         history) are assembled from ONE read of the session, so every
    ///         part of the export describes the same generation. The export
    ///         used to read the session three times — the session record, the
    ///         stats, the messages — and a write landing between those reads
    ///         (a live agent appending, a compaction rewriting the file) put
    ///         the stats of one generation next to the history of another. An
    ///         export is a "send me the log" artefact: a log whose header
    ///         counts disagree with its body misleads whoever reads it.
    ///     </para>
    ///     <para>
    ///         Which instant the snapshot is taken at is not promised — the
    ///         file may have grown a moment later. That does not matter: what
    ///         is promised is that the export is internally consistent.
    ///     </para>
    /// </remarks>
    public async Task<Result> ExportAsync(
        ISessionStore store,
        string sessionId,
        TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(output);

        // #1785: ONE snapshot, from ONE read. Everything below is assembled
        // from it, so the export cannot mix generations.
        var snapshotResult = await ReadSnapshotAsync(store, sessionId, ct).ConfigureAwait(false);
        if (snapshotResult.IsFailure) // §4.6-ok: propagate store diagnostic verbatim.
            return snapshotResult.ConvertFailure();

        SessionSnapshot snapshot = snapshotResult.Value; // guarded: returned above on failure.

        // The stats in the envelope are the stats of the history under it:
        // both come from the same snapshot (the store derives them from the
        // very messages it hands over), so they cannot disagree.
        var envelope = new ExportEnvelope(
            Marker: EnvelopeMarker,
            Version: SchemaVersion,
            Session: snapshot.Session,
            Metadata: snapshot.Session.Metadata);
        await output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonlCodecContext.Default.ExportEnvelope)).ConfigureAwait(false);

        foreach (AgentMessage message in snapshot.Messages)
        {
            var entry = new MessageEntry(
                Type: "message",
                Id: message.Id,
                ParentId: message.ParentId,
                Role: message.Role,
                CreatedAt: message.CreatedAt,
                Payload: JsonlMessageCodec.SerializeMessagePayload(message));
            await output.WriteLineAsync(JsonSerializer.Serialize(entry, JsonlCodecContext.Default.MessageEntry)).ConfigureAwait(false);
        }

        JsonlSessionPorterLog.SessionExported(_logger, sessionId, snapshot.Messages.Count);
        return Result.Success();
    }

    /// <summary>
    ///     One coherent view of the session, from ONE read (<b>#1785</b>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The JSONL store can promise one generation, and is asked for it:
    ///         it reads the file once and takes the header and the history from
    ///         that single pass. A store that cannot is read in two calls — the
    ///         session record (which already carries that store's own stats)
    ///         and the history.
    ///     </para>
    ///     <para>
    ///         The old THIRD call is gone: <c>GetStatsAsync</c> was pure
    ///         duplication. Every store returns the same metadata from
    ///         <c>GetAsync</c> and from <c>GetStatsAsync</c> — the JSONL store
    ///         derives it from the history, the others persist it — so the
    ///         second derivation could only ever disagree with the first, and
    ///         cost one more read of a file that was being written.
    ///     </para>
    /// </remarks>
    private static async Task<Result<SessionSnapshot>> ReadSnapshotAsync(
        ISessionStore store,
        string sessionId,
        CancellationToken ct)
    {
        if (store is JsonlSessionStore jsonl)
        {
            var read = await jsonl.ReadSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
            return read.MapError(e => $"Cannot export session '{sessionId}': {e}");
        }

        var session = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure) // §4.6-ok: propagate store diagnostic verbatim.
            return session.ConvertFailure<SessionSnapshot>().MapError(e => $"Cannot export session '{sessionId}': {e}");
        Session loaded = session.Value; // guarded: returned above on failure.

        var messages = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messages.IsFailure) // §4.6-ok: header written → surface a clear partial-export failure.
            return messages.ConvertFailure<SessionSnapshot>().MapError(e => $"Cannot export messages of session '{sessionId}': {e}");

        return Result.Success(new SessionSnapshot(loaded, messages.Value));
    }

    /// <inheritdoc />
    public async Task<Result<string>> ImportAsync(
        ISessionStore store,
        TextReader input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);

        // #199: the header read is a Result step — an empty payload is a
        // failure, and an I/O error travels the rail instead of throwing.
        var headerLineResult = (await TryReadNonEmptyLineAsync(input).ConfigureAwait(false))
            .MapError(error => $"Import failed while reading header: {error}")
            .Bind(maybe => maybe.HasValue
                ? Result.Success<string>(maybe.Value)
                : Result.Failure<string>("Import failed: payload is empty."));
        if (headerLineResult.IsFailure)
            return headerLineResult;
        string headerLine = headerLineResult.Value; // guarded: returned above on failure.

        var envelopeResult = Result.Try(
            () => JsonSerializer.Deserialize(headerLine, JsonlCodecContext.Default.ExportEnvelope),
            ex => $"Invalid export header: {ex.Message}")
            .Bind(ValidateExportEnvelope);
        if (envelopeResult.IsFailure)
            return envelopeResult.ConvertFailure<string>();
        ExportEnvelope envelope = envelopeResult.Value; // guarded: returned above on failure.

        Session source = envelope.Session;
        // Minted fresh ALWAYS — see ISessionPorter remarks (idempotent double-import).
        // NOTE: stores generate their OWN ids inside CreateAsync, so the enriched
        // target record is derived from created.Value AFTER the call, never before.
        var created = (await store.CreateAsync(source.Directory, source.Agent, source.ProviderId, source.Model, ct)
            .ConfigureAwait(false))
            .MapError(error => $"Import failed while creating session: {error}");
        if (created.IsFailure) // §4.6-ok: single rail-step to the transport type.
            return created.ConvertFailure<string>();

        Session target = created.Value with
        {
            Title = source.Title,
            CreatedAt = source.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            ParentSessionId = source.ParentSessionId,
            Kind = source.Kind
        };
        var linkedResult = await store.UpdateAsync(target, ct).ConfigureAwait(false);
        if (linkedResult.IsFailure)
            JsonlSessionPorterLog.MetadataLinkageFailed(_logger, target.Id, linkedResult.Error);

        int imported = 0, skipped = 0;
        string? line = await ReadBodyLineAsync(input).ConfigureAwait(false);
        while (line is not null)
        {
            ct.ThrowIfCancellationRequested();

            var decode = DecodeMessageLine(target.Id, line);
            if (decode.IsFailure)
            {
                skipped++;
                JsonlSessionPorterLog.SkippedMalformedMessage(_logger, decode.Error);
            }
            else
            {
                #pragma warning disable CFE0001
                // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
                // return or continue, a control-flow shape the analyzer does not model.
                // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
                var appended = await store.AppendMessageAsync(target.Id, decode.Value, ct).ConfigureAwait(false);
                #pragma warning restore CFE0001
                if (appended.IsFailure)
                {
                    skipped++;
                    JsonlSessionPorterLog.PersistImportedMessageFailed(
                        _logger,
                        #pragma warning disable CFE0001
                        // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
                        // return or continue, a control-flow shape the analyzer does not model.
                        // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
                        decode.Value.Id,
                        #pragma warning restore CFE0001
                        appended.Error);
                }
                else
                {
                    imported++;
                }
            }

            line = await ReadBodyLineAsync(input).ConfigureAwait(false);
        }

        if (envelope.Metadata is { } metadata)
        {
            var stats = await store.UpdateStatsAsync(target.Id, metadata, ct).ConfigureAwait(false);
            if (stats.IsFailure)
                JsonlSessionPorterLog.StatsNotRestored(_logger, target.Id, stats.Error);
        }

        JsonlSessionPorterLog.SessionImported(_logger, target.Id, source.Id, imported, skipped);
        return Result.Success(target.Id);
    }

    /// <summary>
    /// Pure header-shape gate for the import preamble: null/marker/version/id
    /// checks in the same order and with the same texts as the inline ladder
    /// they replace, so the <c>Result.Try</c> parse above joins the rail via
    /// <c>Bind</c> and <c>ConvertFailure&lt;string&gt;</c> stays only on the
    /// transport-type boundary below.
    /// </summary>
    private static Result<ExportEnvelope> ValidateExportEnvelope(ExportEnvelope? envelope)
    {
        if (envelope is null || envelope.Marker != EnvelopeMarker)
            return Result.Failure<ExportEnvelope>(
                $"Not a Harbor session export (missing '{EnvelopeMarker}' marker).");
        if (envelope.Version > SchemaVersion)
            return Result.Failure<ExportEnvelope>(
                $"Unsupported export schema version {envelope.Version} (this build supports up to {SchemaVersion}).");
        if (string.IsNullOrWhiteSpace(envelope.Session.Id))
            return Result.Failure<ExportEnvelope>("Export header has no source session id.");
        return Result.Success(envelope);
    }

    /// <summary>
    /// Decode one export body line. Single parse, no <c>Clone()</c> (#180):
    /// <see cref="JsonlMessageCodec.DeserializeMessage"/> runs synchronously
    /// inside the document lifetime, so the intermediate copy was dead weight.
    /// The span fast path (<see cref="JsonlLineParser"/>) stays store-only —
    /// import must tolerate legacy PascalCase aliases, which the span parser
    /// deliberately rejects.
    /// </summary>
    /// <remarks>
    ///     #550: the DECODE sits inside the <c>Result.Try</c>, not in a <c>Bind</c>
    ///     after it. The part factories raise a <see cref="JsonException" /> for a part
    ///     they cannot rebuild, and a throw from inside a <c>Bind</c> lambda escapes the
    ///     <c>Result</c> altogether — so a line carrying one unreadable part would abort
    ///     the whole import instead of skipping that line. It already could: a part
    ///     missing a member used to throw <c>KeyNotFoundException</c> from
    ///     <c>GetProperty</c> on this very path.
    /// </remarks>
    private static Result<AgentMessage> DecodeMessageLine(string sessionId, string line)
    {
        // Result.Try (CSharpFunctionalExtensions 3.7.0) is the library form of
        // wrapping a throwing call in a Result: same catch scope, same text. Both the
        // document parse and the decode it feeds are inside the lambda, so every way a
        // body line can be unreadable arrives on the same rail and is logged as one
        // skipped line.
        //
        // The trailing .Bind(x => x) is the repo's flattening idiom and it is not
        // decoration: 3.7.0 has Try<T>(Func<T>), NOT Try<T>(Func<Result<T>>), so the
        // lambda's own Result is the T here and the rail has to be joined by hand —
        // the same shape SqliteSessionStore.DeleteMessagesAfterAsync uses.
        return Result.Try(
            () =>
            {
                using var doc = JsonDocument.Parse(line);
                return JsonlMessageCodec.DeserializeMessage(sessionId, doc.RootElement);
            },
            ex => $"malformed JSON line: {ex.Message}")
            .Bind(x => x);
    }

    /// <summary>
    ///     Next non-blank line of an import payload. EOF is a successful
    ///     <c>Maybe.None</c> (normal end of the message section) — absence, not
    ///     a value and not a failure, which is why it is a
    ///     <see cref="Maybe{T}" /> rather than a null riding the
    ///     <see cref="Result" /> rail (#199).
    /// </summary>
    private static Task<Result<Maybe<string>>> TryReadNonEmptyLineAsync(TextReader reader) =>
        // Result.Try (CSharpFunctionalExtensions 3.7.0) wraps the throwing read
        // in a Result; the loop then distinguishes EOF (Maybe.None) from a real
        // line (Maybe.From) — absence is not an error, so EOF stays a success.
        Result.Try(
            async () =>
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                        return Maybe<string>.None;
                    if (line.Trim().Length > 0)
                        return Maybe<string>.From(line);
                }
            },
            ex => ex.Message);

    /// <summary>
    ///     Body-line read for the import loop: EOF ends the import, a mid-stream
    ///     I/O error is logged and likewise ends it (best-effort, consistent
    ///     with the malformed-line skip below — the session built so far is kept).
    /// </summary>
    private async Task<string?> ReadBodyLineAsync(TextReader reader)
    {
        var result = await TryReadNonEmptyLineAsync(reader).ConfigureAwait(false);
        if (result.IsFailure)
            JsonlSessionPorterLog.ImportStoppedEarly(_logger, result.Error);
        Maybe<string> line = result.IsSuccess ? result.Value : Maybe<string>.None;
        return line.HasValue ? line.Value : null; // .Value guarded by HasNoValue above.
    }
}

/// <summary>Line-1 envelope. Marker guards against arbitrary-text confusion.</summary>
internal sealed record ExportEnvelope(
    [property: JsonPropertyName("$marker")] string Marker,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("session")] Session Session,
    [property: JsonPropertyName("metadata")] SessionMetadata? Metadata);

/// <summary>
///     SG8: BCL <c>[LoggerMessage]</c> delegates for <see cref="JsonlSessionPorter" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class JsonlSessionPorterLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Exported session {SessionId}: {Count} message(s)")]
    public static partial void SessionExported(ILogger logger, string sessionId, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Imported history into {TargetId} but metadata linkage failed: {Error}")]
    public static partial void MetadataLinkageFailed(ILogger logger, string targetId, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Skipped malformed exported message: {Error}")]
    public static partial void SkippedMalformedMessage(ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to persist imported message {MessageId}: {Error}")]
    public static partial void PersistImportedMessageFailed(ILogger logger, string messageId, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Imported session {SessionId} but stats were not restored: {Error}")]
    public static partial void StatsNotRestored(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Imported session into new id {TargetId} from source {SourceId}: {Imported} message(s), {Skipped} skipped")]
    public static partial void SessionImported(ILogger logger, string targetId, string sourceId, int imported, int skipped);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Import stopped early: message section unreadable: {Error}")]
    public static partial void ImportStoppedEarly(ILogger logger, string error);
}
