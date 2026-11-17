using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
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

    // #2228: tools/list is bounded twice, by tool count and by estimated byte
    // size, so a large plugin catalog yields a finite cursor chain instead of
    // one unbounded frame. MaxPages caps the chain even for a hostile client
    // that keeps asking for the next page forever.
    internal const int DefaultMaxToolsPerPage = 50;
    internal const int DefaultMaxPageBytes = 16 * 1024;
    private const int MaxPages = 1024;

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
    private readonly McpToolListChangeNotifier _listChangeNotifier;
    private readonly int _maxToolsPerPage;
    private readonly int _maxPageBytes;
    private readonly Action _toolsChangedHandler;

    // #2228: client capabilities harvested from the initialize request. Both
    // default to false so a client that never handshakes keeps the old wire
    // shape (no notifications, no structuredContent) and old tests stay green.
    private volatile bool _clientSupportsListChanged;
    private volatile bool _clientSupportsStructuredContent;

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
    /// <param name="maxToolsPerPage">Upper bound on tools in one list page (#2228).</param>
    /// <param name="maxPageBytes">Upper bound on estimated page bytes (#2228).</param>
    /// <param name="notifier">Optional change notifier; injectable so debounce is testable.</param>
    public McpStdioServer(
        McpPluginLoadHost loadHost,
        ILogger<McpStdioServer> logger,
        Action? onSessionEstablished = null,
        PluginHost? pluginHost = null,
        int maxToolsPerPage = DefaultMaxToolsPerPage,
        int maxPageBytes = DefaultMaxPageBytes,
        McpToolListChangeNotifier? notifier = null)
    {
        _loadHost = loadHost ?? throw new ArgumentNullException(nameof(loadHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onSessionEstablished = onSessionEstablished;
        _pluginHost = pluginHost;
        _maxToolsPerPage = maxToolsPerPage > 0 ? maxToolsPerPage : DefaultMaxToolsPerPage;
        _maxPageBytes = maxPageBytes > 0 ? maxPageBytes : DefaultMaxPageBytes;
        _listChangeNotifier = notifier ?? new McpToolListChangeNotifier();

        // #2228: fold every catalog mutation into the debounced notifier. The
        // handler is kept so the subscription can be released if the server is
        // ever torn down independently of the load host.
        _toolsChangedHandler = _listChangeNotifier.SignalChange;
        _loadHost.ToolsChanged += _toolsChangedHandler;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var stdin = new StreamReader(
            Console.OpenStandardInput(), Console.InputEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(), Console.OutputEncoding, bufferSize: 8192, leaveOpen: true) { AutoFlush = true };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // #2228: one writer owns stdout. Both request responses and
        // server-initiated notifications flow through this channel, so a
        // list_changed frame can never interleave with a JSON-RPC response.
        var frames = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        Task writerTask = PumpFramesAsync(frames.Reader, stdout, linked.Token);
        Task notifyTask = PumpNotificationsAsync(frames.Writer, linked.Token);
        try
        {
            string? line;
            while ((line = await stdin.ReadLineAsync(linked.Token).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    string? response = await HandleLineAsync(line, linked.Token).ConfigureAwait(false);
                    if (response is not null)
                        frames.Writer.TryWrite(response);
                }
                catch (Exception ex)
                {
                    McpStdioServerLog.FailedToHandleMessage(_logger, ex);
                }
            }
        }
        finally
        {
            frames.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); } catch { /* shutdown */ }
            linked.Cancel();
            try { await notifyTask.ConfigureAwait(false); } catch { /* shutdown */ }
            _loadHost.ToolsChanged -= _toolsChangedHandler;
        }
    }

    /// <summary>Drain the single stdout writer until the channel completes.</summary>
    private static async Task PumpFramesAsync(ChannelReader<string> reader, StreamWriter stdout, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (reader.TryRead(out string? frame))
                {
                    await stdout.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
                    await stdout.FlushAsync(ct).ConfigureAwait(false);
                }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>
    ///     Poll the debounced notifier and forward due frames to stdout. The
    ///     25 ms tick is only a wake-up interval; the debounce itself lives in
    ///     <see cref="McpToolListChangeNotifier" />.
    /// </summary>
    private async Task PumpNotificationsAsync(ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_clientSupportsListChanged)
                    continue;
                if (_listChangeNotifier.TryTake(out string? frame) && frame is not null)
                {
                    writer.TryWrite(frame);
                    McpStdioServerLog.ListChangedSent(_logger, _listChangeNotifier.CurrentGeneration);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
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
                // #2228: harvest the client's declared capabilities before the
                // result goes out, so list_changed and structuredContent are
                // gated on what the other side actually understands.
                ApplyClientCapabilities(paramsEl);
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
                if (!TryParseCursor(paramsEl, out int offset))
                {
                    await WriteErrorAsync(writer, idEl, -32602, "Invalid tools/list cursor.", ct).ConfigureAwait(false);
                    break;
                }
                await WriteResultAsync(writer, idEl, w => WriteToolsList(w, offset), ct).ConfigureAwait(false);
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
        // #2228: the server does emit list_changed frames, gated on the client
        // having declared the matching capability in its initialize request.
        w.WriteBoolean("listChanged", true);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartObject("serverInfo");
        w.WriteString("name", ServerName);
        w.WriteString("version", ServerVersion);
        w.WriteEndObject();
    }

    /// <summary>
    ///     Harvest the client capability flags from the initialize params.
    ///     Both flags default to false, so a client that never handshakes keeps
    ///     the old wire shape and nothing is emitted behind its back (#2228).
    /// </summary>
    private void ApplyClientCapabilities(JsonElement? paramsEl)
    {
        bool listChanged = false;
        bool structured = false;
        if (paramsEl is { ValueKind: JsonValueKind.Object } p
            && p.TryGetProperty("capabilities", out var caps)
            && caps.ValueKind == JsonValueKind.Object)
        {
            if (caps.TryGetProperty("tools", out var toolCaps) && toolCaps.ValueKind == JsonValueKind.Object)
            {
                if (toolCaps.TryGetProperty("listChanged", out var lc) && lc.ValueKind == JsonValueKind.True)
                    listChanged = true;
                if (toolCaps.TryGetProperty("structuredContent", out var sc) && sc.ValueKind == JsonValueKind.True)
                    structured = true;
            }

            // Harbor-specific opt-in on the standard MCP "experimental" block.
            if (caps.TryGetProperty("experimental", out var exp) && exp.ValueKind == JsonValueKind.Object
                && exp.TryGetProperty("harborStructuredContent", out var hsc) && hsc.ValueKind == JsonValueKind.True)
                structured = true;
        }

        _clientSupportsListChanged = listChanged;
        _clientSupportsStructuredContent = structured;
    }

    /// <summary>
    ///     Parse an opaque tools/list cursor. Absent or null means "first page";
    ///     a non-string or non-negative-integer value is an invalid parameter,
    ///     which the caller maps to JSON-RPC -32602 instead of looping forever.
    /// </summary>
    private static bool TryParseCursor(JsonElement? paramsEl, out int offset)
    {
        offset = 0;
        if (paramsEl is not { ValueKind: JsonValueKind.Object } p)
            return true;
        if (!p.TryGetProperty("cursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null)
            return true;
        if (cursor.ValueKind != JsonValueKind.String)
            return false;

        return int.TryParse(cursor.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out offset);
    }

    /// <summary>
    ///     Write one bounded page of tools/list. The page is capped by count and
    ///     by estimated byte size, and a nextCursor is only emitted while pages
    ///     remain below the hard page cap. Both bounds are what make a large
    ///     catalog terminate (#2228).
    /// </summary>
    private void WriteToolsList(Utf8JsonWriter w, int offset)
    {
        var tools = _loadHost.Tools;
        var names = new List<string>(tools.Count);
        foreach (string name in tools.Keys)
            names.Add(name);
        // Ordinal order makes the numeric cursor stable across calls even
        // though the underlying dictionary has no ordering guarantee.
        names.Sort(StringComparer.Ordinal);

        int index = Math.Min(offset, names.Count);
        int written = 0;
        int bytes = 0;
        int nextIndex = index;
        w.WriteStartArray("tools");
        for (; index < names.Count; index++)
        {
            var tool = tools[names[index]];
            int entryBytes = EstimateToolBytes(names[index], tool.Description, tool.ParameterSchema);
            // Always include at least one entry so a page cannot be empty while
            // tools remain, which would stall a client following the cursor.
            if (written > 0 && (written >= _maxToolsPerPage || bytes + entryBytes > _maxPageBytes))
                break;

            WriteToolEntry(w, names[index], tool);
            bytes += entryBytes;
            written++;
            nextIndex = index + 1;
        }
        w.WriteEndArray();

        int maxTools = checked(_maxToolsPerPage * MaxPages);
        if (nextIndex < names.Count && nextIndex < maxTools)
            w.WriteString("nextCursor", nextIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteToolEntry(Utf8JsonWriter w, string name, ITool tool)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("description", tool.Description);
        w.WritePropertyName("inputSchema");
        WriteSchema(w, tool.ParameterSchema);
        // #2228: advertise a structured output schema so a capable client can
        // bind the structuredContent returned by tools/call without losing types.
        w.WritePropertyName("outputSchema");
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", true);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static int EstimateToolBytes(string name, string description, JsonDocument schema)
    {
        int schemaBytes = schema.RootElement.ValueKind == JsonValueKind.Object
            ? schema.RootElement.GetRawText().Length
            : 16;
        return name.Length + description.Length + schemaBytes + 128;
    }

    /// <summary>
    ///     Write a tool's structured metadata into the MCP result. JsonElement
    ///     and JsonDocument are written verbatim, which preserves every JSON
    ///     type (numbers stay numbers, objects stay objects). Other CLR objects
    ///     go through reflection, which is safe in this JIT-only host.
    /// </summary>
    private static void WriteStructuredContent(Utf8JsonWriter w, object metadata)
    {
        switch (metadata)
        {
            case JsonElement element:
                element.WriteTo(w);
                break;
            case JsonDocument document:
                document.RootElement.WriteTo(w);
                break;
            case string text:
                w.WriteStringValue(text);
                break;
            default:
                JsonSerializer.Serialize(w, metadata, metadata.GetType());
                break;
        }
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
            await WriteToolResultAsync(stdout, idEl, result.Output, result.IsError, result.Metadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            McpStdioServerLog.ToolThrew(_logger, ex, toolName);
            await WriteToolResultAsync(stdout, idEl, $"Tool threw: {ex.Message}", true, null, ct).ConfigureAwait(false);
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

    private async Task WriteToolResultAsync(
        StreamWriter stdout,
        JsonElement idEl,
        string text,
        bool isError,
        object? structuredContent,
        CancellationToken ct)
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
            // #2228: structured payload is emitted only on success, when the
            // tool actually produced metadata, and when the client opted in at
            // handshake. The JSON types survive verbatim (WriteStructuredContent).
            if (!isError && structuredContent is not null && _clientSupportsStructuredContent)
            {
                w.WritePropertyName("structuredContent");
                WriteStructuredContent(w, structuredContent);
            }
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Sent tools/list_changed notification (generation {Generation})")]
    public static partial void ListChangedSent(ILogger logger, long generation);
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
