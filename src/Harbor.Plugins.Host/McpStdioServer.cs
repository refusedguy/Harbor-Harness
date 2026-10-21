using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Hosting;

using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Host;

/// <summary>
///     Minimal, standard MCP stdio server. Speaks JSON-RPC 2.0 NDJSON on stdin/stdout and
///     exposes the <see cref="ITool" />s collected by <see cref="McpPluginLoadHost" />.
///     No Harbor-specific protocol is added — only <c>initialize</c>, <c>tools/list</c>, and
///     <c>tools/call</c> (plus <c>ping</c> and the <c>notifications/initialized</c> no-op),
///     and one namespaced read-only method, <c>harbor/plugins/inventory</c> (#1722).
/// </summary>
internal sealed class McpStdioServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "harbor-csharp-plugins";
    private const string ServerVersion = "0.4.0-alpha";

    /// <summary>
    ///     The namespaced method that answers with the live plugin composition
    ///     (#1722). It is READ-ONLY and it changes nothing: it renders the state
    ///     the host already has.
    /// </summary>
    public const string PluginInventoryMethod = "harbor/plugins/inventory";

    /// <summary>
    /// Frozen empty-JSON-object element for argument-less <c>tools/call</c>
    /// invocations — same convention as <c>AgentLoop.EmptyMalformedArgsElement</c>:
    /// process-lifetime state backed by the source-generated
    /// <see cref="JsonElement"/> info, so the element is self-contained
    /// (no undisposed document is kept alive) and concurrent reads stay
    /// safe because parsed JSON is immutable. Avoids a per-call
    /// <c>Parse("{}") + Clone()</c> (#180).
    /// </summary>
    private static readonly JsonElement EmptyArgs =
        JsonSerializer.Deserialize("{}", McpStdioServerJsonContext.Default.JsonElement);

    private readonly McpPluginLoadHost _loadHost;
    private readonly ILogger<McpStdioServer> _logger;
    private readonly Action? _onSessionEstablished;
    private readonly PluginHost? _pluginHost;

    /// <summary>
    ///     Construct the MCP stdio server over the given sink.
    /// </summary>
    /// <param name="loadHost">The sink the exposed tools come from.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="onSessionEstablished">
    ///     Optional hook fired right after the client completes the MCP handshake
    ///     (#1733). The plugin host uses it to activate the plugins that were waiting for
    ///     the session to be up. A hook that throws is logged and swallowed: a plugin
    ///     problem must not take the protocol loop down with it.
    /// </param>
    /// <param name="pluginHost">
    ///     Optional. The host whose live composition the inventory method reports
    ///     (#1722). Null means the method answers that no composition is available.
    /// </param>
    public McpStdioServer(
        McpPluginLoadHost loadHost,
        ILogger<McpStdioServer> logger,
        Action? onSessionEstablished = null,
        PluginHost? pluginHost = null)
    {
        _loadHost = loadHost ?? throw new ArgumentNullException(nameof(loadHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onSessionEstablished = onSessionEstablished;
        _pluginHost = pluginHost;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var stdin = new StreamReader(
            Console.OpenStandardInput(), Console.InputEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(), Console.OutputEncoding, bufferSize: 8192, leaveOpen: true) { AutoFlush = true };

        string? line;
        while ((line = await stdin.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                await HandleAsync(line, stdout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                McpStdioServerLog.FailedToHandleMessage(_logger, ex);
            }
        }
    }

    private async Task HandleAsync(string line, StreamWriter stdout, CancellationToken ct)
    {
        string? response = await HandleLineAsync(line, ct).ConfigureAwait(false);
        if (response is null)
            return;
        await stdout.WriteAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    ///     Handle one NDJSON frame and return the response frame (with its
    ///     trailing newline), or null when the frame gets no response
    ///     (notifications, unaddressed pings, frames without a method).
    ///     Malformed JSON throws — <see cref="RunAsync" /> logs it per line,
    ///     exactly as the inline DOM path did before this extraction.
    /// </summary>
    internal async Task<string?> HandleLineAsync(string line, CancellationToken ct)
    {
        // #180: envelope pre-scan over pooled UTF-8 bytes. Lines without a
        // string "method" (ignored frames) and the notifications/initialized
        // no-op return before any JsonDocument exists; addressed calls and
        // malformed lines (which RunAsync logs) take the DOM path below.
        if (TryScanMethod(line, out string? scanned))
        {
            if (scanned is null)
                return null;
            if (scanned == "notifications/initialized")
                return null;
        }

        // Same DOM as JsonDocument.Parse, but through the source-generated
        // JsonElement info (PERF-002): the element is self-contained, so the
        // frame no longer pins a document that had to be disposed. Malformed
        // frames still throw JsonException — RunAsync logs them as before.
        JsonElement root = JsonSerializer.Deserialize(line, McpStdioServerJsonContext.Default.JsonElement);
        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return null;

        var method = methodEl.GetString()!;
        bool hasId = root.TryGetProperty("id", out var idEl);
        JsonElement? paramsEl = root.TryGetProperty("params", out var p) ? p : null;

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        switch (method)
        {
            case "initialize":
                await WriteResultAsync(writer, idEl, WriteInitialize, ct).ConfigureAwait(false);
                // #1733: the handshake is the moment the "mcp-session" capability becomes
                // true. The plugins that were waiting for it are activated right here, so
                // their tools are in the very next tools/list.
                _loadHost.MarkSessionEstablished();
                NotifySessionEstablished();
                break;

            case "notifications/initialized":
                return null;

            case "ping":
                if (!hasId)
                    return null;
                await WriteResultAsync(writer, idEl, static w => { }, ct).ConfigureAwait(false);
                break;

            case "tools/list":
                await WriteResultAsync(writer, idEl, WriteToolsList, ct).ConfigureAwait(false);
                break;

            case "tools/call":
                if (!hasId)
                    return null;
                await HandleToolCallAsync(paramsEl, idEl, writer, ct).ConfigureAwait(false);
                break;

            case PluginInventoryMethod:
                // #1722: the live plugin composition. Read-only: it renders the
                // state the host already has and changes nothing.
                if (!hasId)
                    return null;
                await WriteResultAsync(writer, idEl, WritePluginInventory, ct).ConfigureAwait(false);
                break;

            default:
                if (!hasId)
                    return null;
                await WriteErrorAsync(writer, idEl, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                break;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Scan one NDJSON line for a top-level string <c>"method"</c> without
    /// building a DOM. Returns false (defer to the DOM path) on malformed or
    /// trailing-dirty input so error/log behavior is unchanged; otherwise the
    /// method name, or null when the frame carries none. Last-wins on
    /// duplicates, mirroring <c>TryGetProperty</c>.
    /// </summary>
    private static bool TryScanMethod(string line, out string? method)
    {
        method = null;
        int byteCount = Encoding.UTF8.GetByteCount(line);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Encoding.UTF8.GetBytes(line, rented);
            var reader = new Utf8JsonReader(rented.AsSpan(0, byteCount));
            try
            {
                if (!reader.Read())
                    return false; // empty — the DOM path throws, as before
                if (reader.TokenType != JsonTokenType.StartObject)
                    return false; // non-object root: the DOM path's TryGetProperty
                                  // throws and RunAsync logs — preserved verbatim

                bool closed = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        closed = true;
                        break;
                    }

                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;
                    bool isMethod = reader.ValueSpan.SequenceEqual("method"u8);
                    if (!reader.Read())
                        return false; // truncated frame
                    if (isMethod)
                        method = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    reader.Skip();
                }

                if (!closed)
                    return false; // truncated object — the DOM path throws, as before
                return !reader.Read(); // trailing garbage likewise defers
            }
            catch (Exception)
            {
                // Best-effort fast path: anything the scanner does not
                // understand defers to the DOM path, which owns the verdict.
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Fire the session-established hook, if there is one. A hook that throws is
    ///     logged and swallowed: the protocol loop answers the client either way.
    /// </summary>
    private void NotifySessionEstablished()
    {
        if (_onSessionEstablished is null)
        {
            return;
        }

        try
        {
            _onSessionEstablished();
        }
        catch (Exception ex)
        {
            McpStdioServerLog.SessionHookFailed(_logger, ex);
        }
    }

    private void WriteInitialize(Utf8JsonWriter w)
    {
        w.WriteString("protocolVersion", ProtocolVersion);
        w.WriteStartObject("capabilities");
        w.WriteStartObject("tools");
        w.WriteBoolean("listChanged", false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartObject("serverInfo");
        w.WriteString("name", ServerName);
        w.WriteString("version", ServerVersion);
        w.WriteEndObject();
    }

    private void WriteToolsList(Utf8JsonWriter w)
    {
        w.WriteStartArray("tools");
        foreach (var (name, tool) in _loadHost.Tools)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("description", tool.Description);
            w.WritePropertyName("inputSchema");
            WriteSchema(w, tool.ParameterSchema);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>
    ///     Write the live plugin composition into the result object (#1722).
    /// </summary>
    /// <param name="w">The writer to write into.</param>
    /// <remarks>
    ///     The document is the host's own inventory, rendered at the moment the
    ///     method is called: what is loaded, what is pending, what was refused
    ///     and why, which axes each plugin carries and what it registered.
    /// </remarks>
    private void WritePluginInventory(Utf8JsonWriter w)
    {
        if (_pluginHost is null)
        {
            w.WriteString("error", "this host does not publish a plugin inventory");
            return;
        }

        _pluginHost.Inventory.WriteTo(w);
    }

    private static void WriteSchema(Utf8JsonWriter w, JsonDocument schema)
    {
        if (schema.RootElement.ValueKind == JsonValueKind.Object)
            schema.RootElement.WriteTo(w);
        else
            w.WriteRawValue("{\"type\":\"object\"}");
    }

    private async Task HandleToolCallAsync(JsonElement? paramsEl, JsonElement idEl, StreamWriter stdout, CancellationToken ct)
    {
        if (paramsEl is null || !paramsEl.Value.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            await WriteErrorAsync(stdout, idEl, -32602, "tools/call requires 'name'.", ct).ConfigureAwait(false);
            return;
        }

        var toolName = nameEl.GetString()!;
        if (!_loadHost.Tools.TryGetValue(toolName, out var tool))
        {
            await WriteErrorAsync(stdout, idEl, -32602, $"Unknown tool: {toolName}", ct).ConfigureAwait(false);
            return;
        }

        JsonElement args = paramsEl.Value.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object
            ? argsEl.Clone()
            : EmptyArgs;

        var context = BuildContext();
        try
        {
            var result = await tool.ExecuteAsync(args, context, ct).ConfigureAwait(false);
            await WriteToolResultAsync(stdout, idEl, result.Output, result.IsError, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            McpStdioServerLog.ToolThrew(_logger, ex, toolName);
            await WriteToolResultAsync(stdout, idEl, $"Tool threw: {ex.Message}", isError: true, ct).ConfigureAwait(false);
        }
    }

    private static ToolContext BuildContext()
    {
        return new ToolContext(
            SessionId: "harbor-plugins-host",
            MessageId: "0",
            CallId: null,
            Agent: "plugin-host",
            Abort: CancellationToken.None,
            Messages: Array.Empty<AgentMessage>(),
            ReportProgress: (_, _) => Task.CompletedTask,
            Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));
    }

    private async Task WriteToolResultAsync(StreamWriter stdout, JsonElement idEl, string text, bool isError, CancellationToken ct)
    {
        await WriteResultAsync(stdout, idEl, w =>
        {
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("isError", isError);
        }, ct).ConfigureAwait(false);
    }

    private async Task WriteResultAsync(StreamWriter stdout, JsonElement idEl, Action<Utf8JsonWriter> writeResult, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, idEl);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var bytes = ms.ToArray();
        await stdout.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stdout.BaseStream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stdout.BaseStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(StreamWriter stdout, JsonElement idEl, int code, string message, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, idEl);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var bytes = ms.ToArray();
        await stdout.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stdout.BaseStream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stdout.BaseStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteId(Utf8JsonWriter w, JsonElement idEl)
    {
        if (idEl.ValueKind == JsonValueKind.Number)
            w.WriteNumber("id", idEl.GetInt32());
        else if (idEl.ValueKind == JsonValueKind.String)
            w.WriteString("id", idEl.GetString());
        else
            w.WriteNull("id");
    }
}

/// <summary>
///     SG10: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpStdioServer" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class McpStdioServerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to handle MCP message")]
    public static partial void FailedToHandleMessage(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Tool '{Tool}' threw")]
    public static partial void ToolThrew(ILogger logger, Exception ex, string tool);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "The session-established hook threw; the MCP session continues")]
    public static partial void SessionHookFailed(ILogger logger, Exception ex);
}

/// <summary>
///     Source-generated JSON metadata for the stdio MCP envelope path (AOT-safe,
///     PERF-002): raw <see cref="JsonElement"/> frames carry no typed DTOs, so
///     the context exists purely to make envelope parsing reflection-free.
/// </summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class McpStdioServerJsonContext : JsonSerializerContext
{
}
