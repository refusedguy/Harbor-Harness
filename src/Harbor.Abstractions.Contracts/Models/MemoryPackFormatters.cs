using System.Text.Json.Serialization;
using MemoryPack;
namespace Harbor.Abstractions.Models;
/// <summary>
///     Source-generation context for the JSON embedded in MemoryPack payloads.
///     AOT/trim-safe: the NativeAOT compiler (ILC) pre-generates the
///     <see cref="JsonElement" /> converter from this context, so the
///     formatter below never falls back to runtime reflection (IL2026).
/// </summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ContractsJsonContext : JsonSerializerContext
{
}
/// <summary>
///     Custom MemoryPack formatter for <see cref="JsonElement" />.
///     Stores the JSON as a length-prefixed string (UTF-16), parsed back on deserialize.
///     This avoids requiring MemoryPack to understand JSON natively while keeping
///     round-trip semantics correct.
/// </summary>
/// <remarks>
///     Registration is eager via the module initializer below — there is no
///     implicit touch-order dependency on <see cref="ToolCallPart" />. Any
///     MemoryPackable type holding a <see cref="JsonElement" /> member
///     (tool-call args, execution events, permission requests) round-trips
///     regardless of which type is serialized first.
/// </remarks>
public sealed class JsonElementMemoryPackFormatter : MemoryPackFormatter<JsonElement>
{
    /// <inheritdoc />
    /// <remarks>
    ///     The <see cref="JsonElement" /> converter resolves from
    ///     <see cref="ContractsJsonContext" /> (source-generated, trim/AOT-safe);
    ///     the reflection-based Web defaults must not be used here (IL2026
    ///     under NativeAOT).
    /// </remarks>
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref JsonElement value)
    {
        // JsonElement is backed by a pooled JsonDocument; serialize to a string.
        // This is the simplest safe path; for high-throughput scenarios, an
        // UTF-8 based path could be added (requires MemoryPack internal API).
        string json = JsonSerializer.Serialize(value, ContractsJsonContext.Default.JsonElement);
        writer.WriteString(json);
    }

    /// <inheritdoc />
    public override void Deserialize(ref MemoryPackReader reader, scoped ref JsonElement value)
    {
        string? json = reader.ReadString();
        if (string.IsNullOrEmpty(json))
        {
            value = default;
            return;
        }

        // Deserialize<JsonElement> materializes an owned element (backed by its
        // own document), so no Clone()/Dispose dance is needed here.
        value = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.JsonElement);
    }

    /// <summary>
    ///     Register this formatter with the global MemoryPack formatter provider.
    ///     Idempotent; safe to call multiple times.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<JsonElement>())
        {
            MemoryPackFormatterProvider.Register(new JsonElementMemoryPackFormatter());
        }
    }

    // NOTE: no ModuleInitializer here (CA2255: libraries must not use it).
    // Eager registration rides on the ToolCallPart static-constructor hook
    // (see Messages.cs), which runs before any JsonElement holder serializes.
}
