using System.Buffers;
using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>
/// Pooled-buffer JSON-RPC document builders for the MCP call path (#180).
/// Request and params objects are composed with <see cref="Utf8JsonWriter"/>
/// over an <see cref="ArrayBufferWriter{T}"/> and parsed once from UTF-8 —
/// no string-interpolated JSON, no <c>JsonSerializer.Serialize</c> inside
/// interpolation, and no intermediate UTF-16 transcode of the envelope.
/// The returned element is self-contained (the buffer is not retained),
/// so callers use it directly with no disposal. Behavior is byte-compatible with the previous
/// interpolate-then-parse construction (same members, same order, same
/// string escaping — now actually escaped instead of raw-interpolated).
/// </summary>
internal static class McpJsonRpc
{
    /// <summary>Build a <c>{"jsonrpc":"2.0","id":…,"method":…,"params":…}</c> request envelope.</summary>
    public static JsonElement BuildRequest(int id, string method, JsonElement paramsElement)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            paramsElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        // Same DOM as JsonDocument.Parse, but through the source-generated
        // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>Build <c>tools/call</c> params. Missing arguments become <c>{}</c>.</summary>
    public static JsonElement BuildToolCallParams(string toolName, JsonElement arguments)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", toolName);
            writer.WritePropertyName("arguments");
            WriteArgumentsOrEmpty(writer, arguments);
            writer.WriteEndObject();
        }

        // Same DOM as JsonDocument.Parse, but through the source-generated
        // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>Build <c>prompts/get</c> params. Missing arguments become <c>{}</c>.</summary>
    public static JsonElement BuildPromptGetParams(string name, JsonElement arguments)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WritePropertyName("arguments");
            WriteArgumentsOrEmpty(writer, arguments);
            writer.WriteEndObject();
        }

        // Same DOM as JsonDocument.Parse, but through the source-generated
        // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>Build <c>resources/read</c> params.</summary>
    public static JsonElement BuildResourceReadParams(string uri)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteEndObject();
        }

        // Same DOM as JsonDocument.Parse, but through the source-generated
        // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>
    /// Build the RFC7591 dynamic-client-registration body
    /// (<c>redirect_uris</c> / <c>grant_types</c> / <c>scope</c>).
    /// </summary>
    public static JsonElement BuildRegisterClientBody(string redirectUri, string scopes)
    {
        var buffer = new ArrayBufferWriter<byte>(192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("redirect_uris");
            writer.WriteStartArray();
            writer.WriteStringValue(redirectUri);
            writer.WriteEndArray();
            writer.WritePropertyName("grant_types");
            writer.WriteStartArray();
            writer.WriteStringValue("authorization_code");
            writer.WriteStringValue("refresh_token");
            writer.WriteEndArray();
            writer.WriteString("scope", scopes);
            writer.WriteEndObject();
        }

        // Same DOM as JsonDocument.Parse, but through the source-generated
        // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    private static void WriteArgumentsOrEmpty(Utf8JsonWriter writer, JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            arguments.WriteTo(writer);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }
}
