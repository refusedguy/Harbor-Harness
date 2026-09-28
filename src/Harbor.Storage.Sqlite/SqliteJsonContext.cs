// SqliteJsonContext.cs — AOT-compatible JSON metadata for the SQLite session store (#177).
//
// SqliteSessionStore used to serialize every message via the runtime-type overload
// JsonSerializer.Serialize(message, message.GetType(), options): per-call metadata
// lookup (reflection) plus IL2026-unsafe under NativeAOT (§PERF-002). Writes now
// dispatch switch-on-type to the typed overloads below; reads keep the legacy
// role-switch deserializer in SqliteMappers (it is intentionally legacy-tolerant —
// PascalCase fields, numeric enums — so old rows always decode).

using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext" /> covering every type
///     persisted by the SQLite session store. Use the pre-resolved infos in
///     <see cref="SqliteMappers" /> (or <see cref="SqliteMappers.SerializeMessage" />
///     for the <see cref="AgentMessage" /> switch) — never the reflection-based
///     <c>Serialize(object, Type, options)</c> overload.
/// </summary>
/// <remarks>
///     <para>
///         <b>Options parity:</b> the generation-time options replicate the legacy
///         <c>SqliteMappers.JsonOptions</c> semantics — camelCase names,
///         case-insensitive read, nulls omitted — so rows written by the old
///         reflection path and the new source-gen path are byte-identical.
///         The hand-written <c>ContentPartJsonConverter</c> and
///         <c>JsonStringEnumConverter</c> are layered on top of the copied context
///         options (same pattern as <c>ConfigJson.Options</c>): options-level
///         converters win during property resolution, so <c>ContentPart</c>
///         elements keep their legacy <c>{"type":...}</c> shape (no
///         <c>$type</c> discriminator leaks) and <c>StopReason</c> stays a string.
///     </para>
///     <para>
///         <b>Polymorphic root exclusion:</b> <c>ContentPart</c> is deliberately
///         NOT registered even though <c>AssistantMessage.Parts</c> references it.
///         Registering the root is not even the whole story: the
///         <c>[JsonPolymorphic]</c> attributes on the base force a polymorphic
///         type resolver for the <c>Parts</c> property itself, whose derived-type
///         converter lookup hits the options-level
///         <c>ContentPartJsonConverter</c>, which does not support metadata
///         reads/writes (<c>NotSupportedException</c> at first serialization).
///         Instead <c>ContentPartListJsonConverter</c> (exact match for the
///         property type) takes precedence, so element type info is never
///         requested and the resolver is never built; every element still goes
///         through the hand-written per-element converter. The concrete part
///         types stay registered for direct (non-polymorphic) use.
///     </para>
///     <para>
///         <b>Metadata:</b> <see cref="ToolResultEntry.Metadata" /> is
///         <c>object?</c>-typed, which source-gen cannot serve. Non-null values are
///         stripped before serialization (see
///         <see cref="SqliteMappers.WithoutMetadata" />) — the same
///         fidelity-neutral drop the JSONL codec (#51) and MemoryPack
///         (<c>[MemoryPackIgnore]</c>) already apply.
///     </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(ToolResultMessage))]
[JsonSerializable(typeof(SessionMetadata))]
[JsonSerializable(typeof(ToolResultEntry))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(TextPart))]
[JsonSerializable(typeof(ThinkingPart))]
[JsonSerializable(typeof(ToolCallPart))]
[JsonSerializable(typeof(FilePart))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class SqliteJsonContext : JsonSerializerContext;
