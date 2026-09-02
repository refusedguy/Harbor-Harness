using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Host;

/// <summary>
///     Minimal, standard MCP stdio server. Speaks JSON-RPC 2.0 NDJSON on stdin/stdout and
///     exposes the <see cref="ITool" />s collected by <see cref="McpPluginLoadHost" />.
///     No Harbor-specific protocol is added — only <c>initialize</c>, <c>tools/list</c>, and
///     <c>tools/call</c> (plus <c>ping</c> and the <c>notifications/initialized</c> no-op).
/// </summary>
internal sealed class McpStdioServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "harbor-csharp-plugins";
    private const string ServerVersion = "0.4.0-alpha";

    private readonly McpPluginLoadHost _loadHost;
    private readonly ILogger<McpStdioServer> _logger;

    public McpStdioServer(McpPluginLoadHost loadHost, ILogger<McpStdioServer> logger)
    {
        _loadHost = loadHost ?? throw new ArgumentNullException(nameof(loadHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                _logger.LogError(ex, "Failed to handle MCP message");
            }
        }
    }

    private async Task HandleAsync(string line, StreamWriter stdout, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return;

        var method = methodEl.GetString()!;
        bool hasId = root.TryGetProperty("id", out var idEl);
        JsonElement? paramsEl = root.TryGetProperty("params", out var p) ? p : null;

        switch (method)
        {
            case "initialize":
                await WriteResultAsync(stdout, idEl, WriteInitialize, ct).ConfigureAwait(false);
                break;

            case "notifications/initialized":
                return;

            case "ping":
                if (hasId) await WriteResultAsync(stdout, idEl, w => w.WriteStartObject(), ct).ConfigureAwait(false);
                break;

            case "tools/list":
                await WriteResultAsync(stdout, idEl, WriteToolsList, ct).ConfigureAwait(false);
                break;

            case "tools/call":
                if (hasId) await HandleToolCallAsync(paramsEl, idEl, stdout, ct).ConfigureAwait(false);
                break;

            default:
                if (hasId) await WriteErrorAsync(stdout, idEl, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                break;
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
            : JsonDocument.Parse("{}").RootElement.Clone();

        var context = BuildContext();
        try
        {
            var result = await tool.ExecuteAsync(args, context, ct).ConfigureAwait(false);
            await WriteToolResultAsync(stdout, idEl, result.Output, result.IsError, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool '{Tool}' threw", toolName);
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
            Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
            Services: null!);
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
            writeResult(writer);
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
