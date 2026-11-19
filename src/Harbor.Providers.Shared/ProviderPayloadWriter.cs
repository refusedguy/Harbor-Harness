// Shared source: compiled INTO the OpenAI-format-capable provider assemblies
// via <Compile Include> link items (ROP-A ПР.1). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the single-source writer
// travels as a linked file instead of a shared assembly. One source of truth for
// the two request-payload shapes Ollama, OpenAI and OpenAI-Compatible used to
// hand-write in three places (#3786).

using System.Collections.Generic;
using System.Text.Json;
using Harbor.Abstractions.Providers;

namespace Harbor.Providers.Internal;

/// <summary>
///     Minimal payload-writing primitive for the two JSON shapes shared by the
///     Ollama, Chat Completions and Responses-capable request builders (#3786).
/// </summary>
/// <remarks>
///     AOT-safe by construction: every byte goes through
///     <see cref="Utf8JsonWriter" />; no reflection, no <c>JsonSerializer</c>, no
///     untyped carrier. Both methods emit byte-for-byte what the three consumers
///     wrote locally before the extraction, so moving them here is a pure
///     refactor. <c>ProviderPayloadWireTests</c> pins the emitted bytes.
/// </remarks>
internal static class ProviderPayloadWriter
{
    /// <summary>
    ///     Writes the <c>tools</c> array in the Chat Completions / Ollama shape:
    ///     each tool carries a <c>"type":"function"</c> discriminator and its
    ///     fields are nested under <c>function</c>. The Responses API uses a
    ///     different inline shape and keeps its own writer; it does not call this.
    /// </summary>
    public static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("tools");
        for (int i = 0; i < tools.Count; i++)
        {
            ToolDefinition tool = tools[i];
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            tool.InputSchema.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Writes the <c>{"role":…,"content":"…"}</c> message shape used by all
    ///     three consumers for system, fallback-user and plain-text turns.
    /// </summary>
    public static void WriteRoleAndText(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }
}
