// Row/domain mapping for the SQLite store; split from SqliteSessionStore (#184).

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Results;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     Mapping between SQLite rows and domain records. Decode failures are
///     <see cref="Result.Failure{T}" /> so callers can log + skip a bad row
///     without failing the whole history (#199: same warn-and-skip semantics
///     as <c>JsonlSessionStore</c>'s per-line parse).
/// </summary>
internal static class SqliteMappers
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        // #177: copy the source-generated context options (camelCase, case-insensitive
        // read, nulls omitted — parity with the former Web-defaults setup) and layer the
        // hand-written converters on top (same pattern as ConfigJson.Options):
        // options-level converters win, so ContentPart keeps its legacy shape and
        // StopReason stays a string. TypeInfoResolver stays the context alone — no
        // reflection fallback, so missing metadata fails loudly instead of silently.
        // The Web-equivalent naming settings are materialized explicitly: the copied
        // context options bake property names at generation time, so a parameterless
        // enum factory would fall back to a missing policy and write PascalCase.
        var options = new JsonSerializerOptions(SqliteJsonContext.Default.Options)
        {
            TypeInfoResolver = SqliteJsonContext.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new ContentPartJsonConverter());
        options.Converters.Add(ContentPartListJsonConverter.Instance);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>
    ///     Pre-resolved, AOT-safe metadata for the persisted message subtypes and
    ///     session stats. Pass to the <c>JsonTypeInfo&lt;T&gt;</c> serialization
    ///     overloads — never to the reflection-based generic-options overloads.
    /// </summary>
    internal static readonly JsonTypeInfo<UserMessage> UserMessageInfo =
        (JsonTypeInfo<UserMessage>)JsonOptions.GetTypeInfo(typeof(UserMessage));
    internal static readonly JsonTypeInfo<AssistantMessage> AssistantMessageInfo =
        (JsonTypeInfo<AssistantMessage>)JsonOptions.GetTypeInfo(typeof(AssistantMessage));
    internal static readonly JsonTypeInfo<ToolResultMessage> ToolResultMessageInfo =
        (JsonTypeInfo<ToolResultMessage>)JsonOptions.GetTypeInfo(typeof(ToolResultMessage));
    internal static readonly JsonTypeInfo<SessionMetadata> SessionMetadataInfo =
        (JsonTypeInfo<SessionMetadata>)JsonOptions.GetTypeInfo(typeof(SessionMetadata));

    /// <summary>
    ///     Serialize an <see cref="AgentMessage" /> via the source-generated,
    ///     pre-resolved type info for its concrete subtype (#177: replaces the
    ///     runtime-type <c>Serialize(message, message.GetType(), options)</c>
    ///     overload). Output is byte-identical to the former reflection path
    ///     (same options + same converters); the read side
    ///     (<see cref="TryDeserializeMessage" />) is untouched and decodes both.
    /// </summary>
    internal static string SerializeMessage(AgentMessage message) => message switch
    {
        UserMessage u => JsonSerializer.Serialize(u, UserMessageInfo),
        AssistantMessage a => JsonSerializer.Serialize(a, AssistantMessageInfo),
        ToolResultMessage tr => JsonSerializer.Serialize(WithoutMetadata(tr), ToolResultMessageInfo),
        _ => throw new InvalidOperationException(
            $"Unsupported AgentMessage subtype '{message.GetType().FullName}'."),
    };

    /// <summary>
    ///     Strip non-null <see cref="ToolResultEntry.Metadata" /> values: source-gen
    ///     cannot serve <c>object?</c>-typed members, and serializing them would
    ///     throw. Returns the original instance when nothing is stripped (zero
    ///     alloc on the hot path). Same fidelity-neutral drop as the JSONL codec
    ///     (#51) and MemoryPack (<c>[MemoryPackIgnore]</c>); the read side never
    ///     required metadata.
    /// </summary>
    internal static ToolResultMessage WithoutMetadata(ToolResultMessage message)
    {
        var results = message.Results;
        bool any = false;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Metadata is not null)
            {
                any = true;
                break;
            }
        }

        if (!any)
            return message;

        var copy = new ToolResultEntry[results.Count];
        for (int i = 0; i < results.Count; i++)
            copy[i] = results[i] with { Metadata = null };
        return message with { Results = copy };
    }

    /// <summary>
    ///     Decode one message row. A corrupt payload or an unknown role is a
    ///     <see cref="Result.Failure{T}" /> so the caller can log + skip the
    ///     row without failing the whole history (#199: same warn-and-skip
    ///     semantics as <c>JsonlSessionStore</c>'s per-line parse).
    /// </summary>
    internal static Result<AgentMessage> TryDeserializeMessage(string role, string payload)
    {
        // AgentMessage is abstract and has no [JsonDerivedType] discriminator,
        // so we have to pick the concrete type from the role column ourselves.
        return Result.Try(
                () => DeserializeMessage(role, payload),
                ex => $"Message payload parse failed (role '{role}'): {ex.Message}")
            .Bind(m => m is not null
                ? Result.Success<AgentMessage>(m)
                : Result.Failure<AgentMessage>($"Unknown message role '{role}'; row skipped."));
    }

    /// <summary>
    ///     Decode the stored metadata JSON. Unlike the previous
    ///     <c>?? SessionMetadata.Empty</c> fallback, a corrupt document is an
    ///     honest failure naming the session (#199) instead of a silent empty.
    /// </summary>
    internal static Result<SessionMetadata> TryDeserializeMetadata(string json, string sessionId) =>
        Result.Try(
                () => JsonSerializer.Deserialize<SessionMetadata>(json, JsonOptions),
                ex => $"Session '{sessionId}' metadata is corrupt: {ex.Message}")
            .Bind(m => m is not null
                ? Result.Success<SessionMetadata>(m)
                : Result.Failure<SessionMetadata>($"Session '{sessionId}' metadata is corrupt (null document)."));

    internal static AgentMessage? DeserializeMessage(string role, string payload)
    {
        return role switch
        {
            "user" => JsonSerializer.Deserialize<UserMessage>(payload, JsonOptions),
            "assistant" => JsonSerializer.Deserialize<AssistantMessage>(payload, JsonOptions),
            "tool_result" => JsonSerializer.Deserialize<ToolResultMessage>(payload, JsonOptions),
            _ => null
        };
    }

    internal static Session ReadSession(DbDataReader reader)
    {
        string id = reader.GetString(reader.GetOrdinal("id"));
        string projectId = reader.GetString(reader.GetOrdinal("project_id"));
        string directory = reader.GetString(reader.GetOrdinal("directory"));
        string title = reader.GetString(reader.GetOrdinal("title"));
        string agent = reader.GetString(reader.GetOrdinal("agent"));
        string model = reader.GetString(reader.GetOrdinal("model"));
        string providerId = reader.GetString(reader.GetOrdinal("provider_id"));
        var createdAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")));
        string metaJson = reader.GetString(reader.GetOrdinal("metadata"));
        var meta = JsonSerializer.Deserialize<SessionMetadata>(metaJson, JsonOptions) ?? SessionMetadata.Empty;

        return new Session(
            id,
            projectId,
            directory,
            title,
            agent,
            model,
            providerId,
            createdAt,
            updatedAt,
            meta);
    }

    internal sealed class ContentPartJsonConverter : JsonConverter<ContentPart>
    {
        internal static readonly ContentPartJsonConverter Instance = new();

        /// <summary>
        ///     #550 — a tag → type factory, not a walk over the part union, so there is
        ///     no visitor to route it through and nothing downstream can notice a tag it
        ///     does not know. It used to answer <see langword="null" /> and let the
        ///     enclosing array converter skip the null, which is how a row written by a
        ///     newer Harbor reloaded as a shorter assistant turn with nothing logged.
        /// </summary>
        /// <remarks>
        ///     Now it refuses, by name, exactly like
        ///     <see cref="ContentPartVisitor{TResult}.Accept" /> refuses a kind the write
        ///     side has no arm for: an unknown tag is version skew, and a known tag
        ///     missing a field is a corrupt row. Neither is a part this reader may
        ///     invent. The store collects a refused row into its <c>skipped</c> list and
        ///     <c>LogWarning</c>s the reason, so the cost of refusing is one warning that
        ///     names the tag; the cost of the old behaviour was a part nobody could
        ///     account for. The <see cref="JsonException" /> is what
        ///     <see cref="TryDeserializeMessage" />'s <c>Result.Try</c> turns into that
        ///     failure, so this is the same rail the rest of the row decode rides.
        /// </remarks>
        public override ContentPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var el = doc.RootElement;
            string? type = el.TryGetProperty("type", out var tp) ? tp.GetString() : el.TryGetProperty("Type", out var tp2) ? tp2.GetString() : null;
            return type switch
            {
                "text" => new TextPart(RequiredString(el, "text")),
                "thinking" => new ThinkingPart(RequiredString(el, "text")),
                "tool_call" => new ToolCallPart(
                    RequiredString(el, "id"),
                    RequiredString(el, "toolName"),
                    // A row with no args at all is tolerated, as it always was: an
                    // undefined element is a faithful reading of "this row carries no
                    // arguments", and refusing it would truncate a row this build
                    // used to load.
                    el.TryGetProperty("args", out var a) ? a.Clone() : el.TryGetProperty("Args", out var a2) ? a2.Clone() : default),
                "file" => new FilePart(
                    RequiredString(el, "path"),
                    RequiredString(el, "mimeType"),
                    RequiredInt64(el, "sizeBytes")),
                null => throw new JsonException(
                    "content part has no 'type' discriminator, so it cannot be rebuilt."),
                _ => throw new JsonException(
                    $"content part type '{type}' is not known to this build; the row was refused rather than read with the part missing."),
            };
        }

        /// <summary>
        ///     A mandatory string member, tolerating the pre-camelCase casing rows
        ///     written before the naming policy carry. A member that is absent under
        ///     both spellings is a named <see cref="JsonException" /> rather than the
        ///     <see cref="KeyNotFoundException" /> <c>GetProperty</c> used to throw —
        ///     the whole point of refusing is that the refusal says which part of which
        ///     row is broken.
        /// </summary>
        private static string RequiredString(JsonElement el, string camel)
        {
            if (el.TryGetProperty(camel, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString()!;

            string legacy = char.ToUpperInvariant(camel[0]) + camel[1..];
            if (el.TryGetProperty(legacy, out var legacyValue) && legacyValue.ValueKind == JsonValueKind.String)
                return legacyValue.GetString()!;

            throw new JsonException($"content part of type '{TagOf(el)}' is missing '{camel}'.");
        }

        private static long RequiredInt64(JsonElement el, string camel)
        {
            if (el.TryGetProperty(camel, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetInt64();

            string legacy = char.ToUpperInvariant(camel[0]) + camel[1..];
            if (el.TryGetProperty(legacy, out var legacyValue) && legacyValue.ValueKind == JsonValueKind.Number)
                return legacyValue.GetInt64();

            throw new JsonException($"content part of type '{TagOf(el)}' is missing '{camel}'.");
        }

        /// <summary>The part's own tag, for a diagnostic that has to name the part.</summary>
        private static string TagOf(JsonElement el) =>
            (el.TryGetProperty("type", out var tp) ? tp.GetString()
                : el.TryGetProperty("Type", out var tp2) ? tp2.GetString()
                : null) ?? "<missing>";

        /// <summary>
        ///     #461: the write side is a per-kind walk, so it goes through
        ///     <see cref="ContentPartVisitor{TResult}" />. A part kind the
        ///     serializer was never taught now throws instead of being written
        ///     out as an empty <c>{}</c> object that no reader can decode.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, ContentPart value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            new PartWriter(writer, options).Accept(value);
            writer.WriteEndObject();
        }

        /// <summary>
        ///     Per-kind JSON shape for one <see cref="ContentPart" />. The property
        ///     names and the order the old switch wrote them in are the on-disk
        ///     contract — unchanged.
        /// </summary>
        private sealed class PartWriter(Utf8JsonWriter writer, JsonSerializerOptions options)
            : ContentPartVisitor<PartWriter>
        {
            public override PartWriter Visit(TextPart part)
            {
                writer.WriteString("type", "text");
                writer.WriteString("text", part.Text);
                return this;
            }

            public override PartWriter Visit(ThinkingPart part)
            {
                writer.WriteString("type", "thinking");
                writer.WriteString("text", part.Text);
                return this;
            }

            public override PartWriter Visit(ToolCallPart part)
            {
                writer.WriteString("type", "tool_call");
                writer.WriteString("id", part.Id);
                writer.WriteString("toolName", part.ToolName);
                writer.WritePropertyName("args");
                JsonSerializer.Serialize(writer, part.Args, options);
                return this;
            }

            public override PartWriter Visit(FilePart part)
            {
                writer.WriteString("type", "file");
                writer.WriteString("path", part.Path);
                writer.WriteString("mimeType", part.MimeType);
                writer.WriteNumber("sizeBytes", part.SizeBytes);
                return this;
            }
        }
    }
}
