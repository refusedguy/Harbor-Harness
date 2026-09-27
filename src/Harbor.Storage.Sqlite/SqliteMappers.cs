// SqliteMappers.cs — row ↔ domain mapping for the SQLite store.
//
// Extracted verbatim from SqliteSessionStore.cs (#184 god-object
// decomposition). The store owns connection lifetime + per-session locking;
// this file owns the mapping component: session-row reads, message-payload
// decode (role → concrete type), metadata decode, and the JSON options with
// the polymorphic ContentPart converter.

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new ContentPartJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
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

    private sealed class ContentPartJsonConverter : JsonConverter<ContentPart>
    {
        public override ContentPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var el = doc.RootElement;
            string? type = el.TryGetProperty("type", out var tp) ? tp.GetString() : el.TryGetProperty("Type", out var tp2) ? tp2.GetString() : null;
            return type switch
            {
                "text" => new TextPart(el.TryGetProperty("text", out var t) ? t.GetString()! : el.GetProperty("Text").GetString()!),
                "thinking" => new ThinkingPart(el.TryGetProperty("text", out var t) ? t.GetString()! : el.GetProperty("Text").GetString()!),
                "tool_call" => new ToolCallPart(
                    el.TryGetProperty("id", out var id) ? id.GetString()! : el.GetProperty("Id").GetString()!,
                    el.TryGetProperty("toolName", out var tn) ? tn.GetString()! : el.GetProperty("ToolName").GetString()!,
                    el.TryGetProperty("args", out var a) ? a.Clone() : el.TryGetProperty("Args", out var a2) ? a2.Clone() : default),
                "file" => new FilePart(
                    el.TryGetProperty("path", out var p) ? p.GetString()! : el.GetProperty("Path").GetString()!,
                    el.TryGetProperty("mimeType", out var mt) ? mt.GetString()! : el.GetProperty("MimeType").GetString()!,
                    el.TryGetProperty("sizeBytes", out var sb) ? sb.GetInt64() : el.GetProperty("SizeBytes").GetInt64()),
                _ => null
            };
        }

        public override void Write(Utf8JsonWriter writer, ContentPart value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case TextPart t:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", t.Text);
                    break;
                case ThinkingPart th:
                    writer.WriteString("type", "thinking");
                    writer.WriteString("text", th.Text);
                    break;
                case ToolCallPart tc:
                    writer.WriteString("type", "tool_call");
                    writer.WriteString("id", tc.Id);
                    writer.WriteString("toolName", tc.ToolName);
                    writer.WritePropertyName("args");
                    JsonSerializer.Serialize(writer, tc.Args, options);
                    break;
                case FilePart f:
                    writer.WriteString("type", "file");
                    writer.WriteString("path", f.Path);
                    writer.WriteString("mimeType", f.MimeType);
                    writer.WriteNumber("sizeBytes", f.SizeBytes);
                    break;
            }
            writer.WriteEndObject();
        }
    }
}
