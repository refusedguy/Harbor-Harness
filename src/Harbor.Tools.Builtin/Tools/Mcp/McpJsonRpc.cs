using System.Buffers;
using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>
/// Pooled-buffer JSON-RPC document builders for the MCP call path (#180).
/// Request and params objects are composed with <see cref="Utf8JsonWriter"/>
/// over an <see cref="ArrayBufferWriter{T}"/> and parsed once from UTF-8 —
/// no string-interpolated JSON, no <c>JsonSerializer.Serialize</c> inside
/// interpolation, and no intermediate UTF-16 transcode of the envelope.
/// A <c>tools/call</c> builder may add one <c>_meta.traceparent</c> member when
/// the caller supplies a trace parent (#2678 item 113); without one the wire
/// shape is byte-identical to the historical envelope.
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

    /// <summary>
    /// Build <c>tools/list</c> params. A null cursor asks for the first page; a
    /// cursor asks for the page the server named in its previous
    /// <c>nextCursor</c> (#2228 pagination).
    /// </summary>
    public static JsonElement BuildToolsListParams(string? cursor)
    {
        if (cursor is null)
        {
            // Same convention as the empty-element fast path: no cursor member.
            var firstPage = new ArrayBufferWriter<byte>(2);
            using (var writer = new Utf8JsonWriter(firstPage))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            return JsonSerializer.Deserialize(firstPage.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
        }

        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("cursor", cursor);
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>Build <c>tools/call</c> params. Missing arguments become <c>{}</c>.</summary>
    /// <remarks>
    ///     #2678 item 113: a non-empty <paramref name="traceparent"/> is written
    ///     to <c>_meta.traceparent</c> so an out-of-process MCP server can join
    ///     the agent's trace. When it is absent the wire shape is unchanged:
    ///     no <c>_meta</c> member, same bytes as before this option existed.
    /// </remarks>
    public static JsonElement BuildToolCallParams(string toolName, JsonElement arguments, string? traceparent = null)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", toolName);
            writer.WritePropertyName("arguments");
            WriteArgumentsOrEmpty(writer, arguments);
            if (!string.IsNullOrEmpty(traceparent))
            {
                writer.WritePropertyName("_meta");
                writer.WriteStartObject();
                writer.WriteString("traceparent", traceparent);
                writer.WriteEndObject();
            }
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

    /// <summary>
    /// Build the client <c>initialize</c> params for #2661: declare roots
    /// (<c>listChanged</c>) and elicitation as client capabilities. Same
    /// AOT-safe construction as every other builder here — Utf8JsonWriter over
    /// a pooled buffer, parsed once through the source-generated JsonElement
    /// info — so no reflection and no string-interpolated JSON.
    /// </summary>
    public static JsonElement BuildInitializeParams(string protocolVersion = "2024-11-05")
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", protocolVersion);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            // roots: the client exposes its working directory and wants to be
            // told when the server's root set should be refreshed.
            writer.WritePropertyName("roots");
            writer.WriteStartObject();
            writer.WriteBoolean("listChanged", true);
            writer.WriteEndObject();
            // elicitation: an empty object is the spec's way of declaring the
            // capability (slice 2 adds the responder behind it).
            writer.WritePropertyName("elicitation");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("clientInfo");
            writer.WriteStartObject();
            writer.WriteString("name", "harbor");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>
    /// Build a method-only JSON-RPC notification frame (no <c>id</c>). Used for
    /// <c>notifications/initialized</c> and <c>notifications/roots/list_changed</c>.
    /// </summary>
    public static JsonElement BuildNotification(string method)
    {
        var buffer = new ArrayBufferWriter<byte>(96);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>Build a <c>roots/list</c> response containing one file:// root.</summary>
    public static JsonElement BuildRootsListResult(string directory)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("roots");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("uri", ToFileUri(directory));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    /// <summary>Build a JSON-RPC response, echoing the server's id verbatim (number or string).</summary>
    public static JsonElement BuildResponse(JsonElement id, JsonElement result)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenMemory.Span, McpJsonSerializerContext.Default.JsonElement);
    }

    private static string ToFileUri(string directory)
    {
        string full = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(directory);
        return new Uri(full, UriKind.Absolute).AbsoluteUri;
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
