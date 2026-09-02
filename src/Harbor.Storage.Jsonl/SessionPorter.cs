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
/// </remarks>
public sealed class JsonlSessionPorter : ISessionPorter
{
    /// <summary>Bump on any envelope-shape change; ImportAsync rejects newer majors.</summary>
    internal const int SchemaVersion = 1;

    private static readonly string EnvelopeMarker = "$harbor-session-export";

    private readonly ILogger<JsonlSessionPorter> _logger;

    public JsonlSessionPorter(ILogger<JsonlSessionPorter> logger) => _logger = logger;

    /// <inheritdoc />
    public async Task<Result> ExportAsync(
        ISessionStore store,
        string sessionId,
        TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(output);

        var session = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure) // §4.6-ok: propagate store diagnostic verbatim.
            return Result.Failure($"Cannot export session '{sessionId}': {session.Error}");

        var stats = await store.GetStatsAsync(sessionId, ct).ConfigureAwait(false);

        var envelope = new ExportEnvelope(
            Marker: EnvelopeMarker,
            Version: SchemaVersion,
            Session: session.Value,
            Metadata: stats.IsSuccess ? stats.Value : null);
        await output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonlCodecContext.JsonOptions)).ConfigureAwait(false);

        var messages = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messages.IsFailure) // §4.6-ok: header written → surface a clear partial-export failure.
            return Result.Failure($"Cannot export messages of session '{sessionId}': {messages.Error}");

        foreach (AgentMessage message in messages.Value)
        {
            var entry = new MessageEntry(
                Type: "message",
                Id: message.Id,
                ParentId: message.ParentId,
                Role: message.Role,
                CreatedAt: message.CreatedAt,
                Payload: JsonlMessageCodec.SerializeMessagePayload(message));
            await output.WriteLineAsync(JsonSerializer.Serialize(entry, JsonlCodecContext.JsonOptions)).ConfigureAwait(false);
        }

        _logger.LogInformation("Exported session {SessionId}: {Count} message(s)",
            sessionId, messages.Value.Count);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<string>> ImportAsync(
        ISessionStore store,
        TextReader input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);

        string? headerLine = await ReadNonEmptyLineAsync(input).ConfigureAwait(false);
        if (headerLine is null)
            return Result.Failure<string>("Import failed: payload is empty.");

        var envelopeResult = Result.Try(
            () => JsonSerializer.Deserialize<ExportEnvelope>(headerLine, JsonlCodecContext.JsonOptions),
            ex => $"Invalid export header: {ex.Message}");
        if (envelopeResult.IsFailure)
            return Result.Failure<string>(envelopeResult.Error);
        ExportEnvelope? envelope = envelopeResult.Value;

        if (envelope is null || envelope.Marker != EnvelopeMarker)
            return Result.Failure<string>(
                $"Not a Harbor session export (missing '{EnvelopeMarker}' marker).");
        if (envelope.Version > SchemaVersion)
            return Result.Failure<string>(
                $"Unsupported export schema version {envelope.Version} (this build supports up to {SchemaVersion}).");
        if (string.IsNullOrWhiteSpace(envelope.Session.Id))
            return Result.Failure<string>("Export header has no source session id.");

        Session source = envelope.Session;
        // Minted fresh ALWAYS — see ISessionPorter remarks (idempotent double-import).
        // NOTE: stores generate their OWN ids inside CreateAsync, so the enriched
        // target record is derived from created.Value AFTER the call, never before.
        var created = await store.CreateAsync(source.Directory, source.Agent, source.ProviderId, source.Model, ct)
            .ConfigureAwait(false);
        if (created.IsFailure) // §4.6-ok: single rail-step to the transport type.
            return Result.Failure<string>($"Import failed while creating session: {created.Error}");

        Session target = created.Value with
        {
            Title = source.Title,
            CreatedAt = source.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            ParentSessionId = source.ParentSessionId
        };
        var linkedResult = await store.UpdateAsync(target, ct).ConfigureAwait(false);
        if (linkedResult.IsFailure)
            _logger.LogWarning("Imported history into {TargetId} but metadata linkage failed: {Error}",
                target.Id, linkedResult.Error);

        int imported = 0, skipped = 0;
        string? line = await ReadNonEmptyLineAsync(input).ConfigureAwait(false);
        while (line is not null)
        {
            ct.ThrowIfCancellationRequested();

            var decode = DecodeMessageLine(target.Id, line);
            if (decode.IsFailure)
            {
                skipped++;
                _logger.LogWarning("Skipped malformed exported message: {Error}", decode.Error);
            }
            else
            {
                var appended = await store.AppendMessageAsync(target.Id, decode.Value, ct).ConfigureAwait(false);
                if (appended.IsFailure)
                {
                    skipped++;
                    _logger.LogWarning("Failed to persist imported message {MessageId}: {Error}",
                        decode.Value.Id, appended.Error);
                }
                else
                {
                    imported++;
                }
            }

            line = await ReadNonEmptyLineAsync(input).ConfigureAwait(false);
        }

        if (envelope.Metadata is { } metadata)
        {
            var stats = await store.UpdateStatsAsync(target.Id, metadata, ct).ConfigureAwait(false);
            if (stats.IsFailure)
                _logger.LogWarning("Imported session {SessionId} but stats were not restored: {Error}",
                    target.Id, stats.Error);
        }

        _logger.LogInformation("Imported session into new id {TargetId} from source {SourceId}: {Imported} message(s), {Skipped} skipped",
            target.Id, source.Id, imported, skipped);
        return Result.Success(target.Id);
    }

    private static Result<AgentMessage> DecodeMessageLine(string sessionId, string line) =>
        Result.Try(
                () => JsonDocument.Parse(line).RootElement.Clone(),
                ex => $"malformed JSON line: {ex.Message}")
            .Bind(element => JsonlMessageCodec.DeserializeMessage(sessionId, element));

    private static async Task<string?> ReadNonEmptyLineAsync(TextReader reader)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Trim().Length > 0)
                return line;
        }
    }
}

/// <summary>Line-1 envelope. Marker guards against arbitrary-text confusion.</summary>
internal sealed record ExportEnvelope(
    [property: JsonPropertyName("$marker")] string Marker,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("session")] Session Session,
    [property: JsonPropertyName("metadata")] SessionMetadata? Metadata);
