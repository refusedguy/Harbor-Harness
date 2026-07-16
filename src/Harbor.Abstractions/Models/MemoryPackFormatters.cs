using System.Text.Json;
using MemoryPack;
using MemoryPack.Formatters;

namespace Harbor.Abstractions.Models;

/// <summary>
/// Custom MemoryPack formatter for <see cref="JsonElement"/>.
/// Stores the JSON as a length-prefixed string (UTF-16), parsed back on deserialize.
/// This avoids requiring MemoryPack to understand JSON natively while keeping
/// round-trip semantics correct.
/// </summary>
/// <remarks>
/// This formatter is registered lazily via the static constructor hook on
/// <see cref="ToolCallPart"/> (MemoryPack's <c>static partial void StaticConstructor()</c>),
/// so any MemoryPackable type that includes a <see cref="JsonElement"/> member will
/// pick it up automatically once <see cref="ToolCallPart"/> is touched.
/// </remarks>
public sealed class JsonElementMemoryPackFormatter : MemoryPackFormatter<JsonElement>
{
    /// <inheritdoc />
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref JsonElement value)
    {
        // JsonElement is backed by a pooled JsonDocument; serialize to a string.
        // This is the simplest safe path; for high-throughput scenarios, an
        // UTF-8 based path could be added (requires MemoryPack internal API).
        var json = JsonSerializer.Serialize(value);
        writer.WriteString(json);
    }

    /// <inheritdoc />
    public override void Deserialize(ref MemoryPackReader reader, scoped ref JsonElement value)
    {
        var json = reader.ReadString();
        if (string.IsNullOrEmpty(json))
        {
            value = default;
            return;
        }

        // Parse and Clone to detach from the underlying JsonDocument (which we dispose below).
        using var doc = JsonDocument.Parse(json);
        value = doc.RootElement.Clone();
    }

    /// <summary>
    /// Register this formatter with the global MemoryPack formatter provider.
    /// Idempotent; safe to call multiple times.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<JsonElement>())
        {
            MemoryPackFormatterProvider.Register<JsonElement>(new JsonElementMemoryPackFormatter());
        }
    }
}
