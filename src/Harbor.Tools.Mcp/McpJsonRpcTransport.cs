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
        _reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 8192, leaveOpen: true) { AutoFlush = true };
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task WriteAsync(JsonElement message, CancellationToken ct = default)
    {
        await _writer.WriteAsync(message.GetRawText().AsMemory(), ct).ConfigureAwait(false);
        await _writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument?> ReadAsync(CancellationToken ct = default)
    {
        string? line;
        while ((line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            return JsonDocument.Parse(line);
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
