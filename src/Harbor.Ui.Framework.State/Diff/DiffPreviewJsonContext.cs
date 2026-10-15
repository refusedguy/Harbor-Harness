using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     AOT-safe serializer context for the diff tool-args probe.
///     Args are per-tool dynamic JSON with no static record shape, so they
///     decode as <see cref="JsonElement" /> — the same DOM
///     <c>JsonDocument.Parse</c> produced, but through a source-generated
///     <c>JsonTypeInfo</c> (PERF-002: only writer/context on hot paths;
///     arch-gate TRIM-SERIALIZATION TypeInfo spelling).
/// </summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DiffPreviewJsonContext : JsonSerializerContext
{
}
