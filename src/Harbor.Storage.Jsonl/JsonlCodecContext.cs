// JsonlCodecContext.cs — AOT-compatible JsonSerializerContext for JSONL session
// storage. Every concrete type serialized by Harbor.Storage.Jsonl is registered
// here via [JsonSerializable] so the NativeAOT compiler (ILC) can pre-generate
// the serialization code and the trimmer keeps the types.
//
// Anonymous types from the old SerializeMessagePayload/SerializePart paths have
// been replaced with named DTO records (UserPayload, AssistantPayload, etc.) so
// they can be registered here.

using System.Text.Json.Serialization;
namespace Harbor.Storage.Jsonl;

/// <summary>
///     AOT-safe <see cref="JsonSerializerContext" /> for all types serialized by
///     the JSONL session store and message codec. Replace reflection-based
///     <c>JsonSerializer.Serialize&lt;T&gt;</c> / <c>JsonSerializer.Deserialize&lt;T&gt;</c>
///     calls with the <c>JsonTypeInfo</c>-based overloads from this context.
/// </summary>
/// <remarks>
///     Naming policy (V4-bugfix): <b>camelCase at context level</b>. Before this policy,
///     types WITHOUT explicit <see cref="System.Text.Json.Serialization.JsonPropertyAttribute" />
///     (domain <c>Usage</c>, <c>ToolResultEntry</c>) were WRITTEN PascalCase through
///     <c>JsonlCodecContext.Default.*</c> but READ back by the hand-rolled camelCase
///     extractors in <see cref="JsonlMessageCodec" /> — every persisted tool_result
///     message silently vanished on reload and usage numbers could be dropped. With a
///     context-level policy both paths emit/expect identical camelCase; explicit
///     attribute names keep priority over the policy.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionHeaderEntry))]
[JsonSerializable(typeof(MessageEntry))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(SessionMetadata))]
[JsonSerializable(typeof(ExportEnvelope))]
[JsonSerializable(typeof(ToolResultEntry))]
[JsonSerializable(typeof(TextPartPayload))]
[JsonSerializable(typeof(ThinkingPartPayload))]
[JsonSerializable(typeof(ToolCallPartPayload))]
[JsonSerializable(typeof(FilePartPayload))]
[JsonSerializable(typeof(UnknownPartPayload))]
[JsonSerializable(typeof(UserPayload))]
[JsonSerializable(typeof(AssistantPayload))]
[JsonSerializable(typeof(ToolResultPayload))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class JsonlCodecContext : JsonSerializerContext
{
    /// <summary>
    ///     Pre-configured options that include this context as the
    ///     <see cref="JsonSerializerOptions.TypeInfoResolver" />, so that
    ///     <c>object</c>-typed properties (e.g. <see cref="MessageEntry.Payload" />)
    ///     resolve their runtime types through the generated type info at
    ///     runtime — no reflection needed.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = Default
    };
}

// ── Payload DTOs (replace anonymous types so they can be AOT-registered) ────

internal sealed record UserPayload(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("model")] string Model);

internal sealed record AssistantPayload(
    [property: JsonPropertyName("parts")] object[] Parts,
    [property: JsonPropertyName("stopReason")] string StopReason,
    [property: JsonPropertyName("usage")] Usage Usage,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("isSummary")] bool IsSummary,
    [property: JsonPropertyName("summaryFirstKeptId")] string? SummaryFirstKeptId);

internal sealed record ToolResultPayload(
    [property: JsonPropertyName("results")] ToolResultEntry[] Results);

internal sealed record TextPartPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record ThinkingPartPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record ToolCallPartPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("args")] JsonElement Args);

internal sealed record FilePartPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

internal sealed record UnknownPartPayload(
    [property: JsonPropertyName("type")] string Type);
