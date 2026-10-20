// BenchmarkJsonContext.cs — the source-generated context for the OpenAI-shaped
// payloads the benchmarks build (#1822).
//
// WHY IT IS PROJECT-LOCAL
// -----------------------
// The payload the benchmarks measure is an OpenAI tool-call envelope: a
// `tool_calls` array whose entries carry a JSON `arguments` OBJECT. That
// shape is the provider's wire format, not a tool-argument payload, so it
// does not belong in the shared kit's tool-argument family.
//
// The nested `arguments` object keeps the three members it had — `path`,
// `limit`, `offset` — with the same names and the same order, so the
// benchmark still measures the same ~4 KB document it measured before.

using System.Text.Json.Serialization;

namespace Harbor.Benchmarks;

/// <summary>     The envelope that carries the array of tool calls. </summary>
public sealed record ToolCallsEnvelope(
    [property: JsonPropertyName("tool_calls")] ToolCallEnvelope[] ToolCalls);

/// <summary>     One tool call of the envelope. </summary>
public sealed record ToolCallEnvelope(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] ToolCallArgumentsEnvelope Arguments);

/// <summary>     The arguments object of one tool call. </summary>
public sealed record ToolCallArgumentsEnvelope(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] int Offset);

/// <summary>
///     Source-generated metadata for the benchmark's OpenAI-shaped payloads.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ToolCallsEnvelope), TypeInfoPropertyName = "ToolCallsEnvelope")]
[JsonSerializable(typeof(ToolCallEnvelope), TypeInfoPropertyName = "ToolCallEnvelope")]
[JsonSerializable(typeof(ToolCallArgumentsEnvelope), TypeInfoPropertyName = "ToolCallArgumentsEnvelope")]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext
{
}
