using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Tools.Mcp;

internal sealed class McpJsonRpcTransport : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _options;
    private bool _disposed;

    public McpJsonRpcTransport(Stream input, Stream output, JsonSerializerOptions? options = null)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true) { AutoFlush = true };
        _options = options ?? new JsonSerializerOptions
        {
            TypeInfoResolver = McpJsonSerializerContext.Default,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task WriteAsync(JsonElement message, CancellationToken ct = default)
    {
        await _writer.WriteAsync(message.GetRawText().AsMemory(), ct).ConfigureAwait(false);
        await _writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
    }

    public async Task<JsonElement?> ReadAsync(CancellationToken ct = default)
    {
        string? line;
        while ((line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Same DOM as JsonDocument.Parse, but through the source-generated
            // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
            JsonElement element = JsonSerializer.Deserialize(line, McpJsonSerializerContext.Default.JsonElement);
            // #2228: a server-initiated notification (a frame with a method and
            // no id, e.g. notifications/tools/list_changed) is not the answer to
            // a request. Skip it so a list_changed frame can never be mistaken
            // for the response the caller is waiting on.
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("method", out var methodEl)
                && methodEl.ValueKind == JsonValueKind.String
                && !element.TryGetProperty("id", out _))
                continue;
            return element;
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _reader.Dispose();
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }
}
