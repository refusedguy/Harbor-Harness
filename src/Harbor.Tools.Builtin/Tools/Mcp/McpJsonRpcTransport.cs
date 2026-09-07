using System.Text;
using System.Text.Json.Serialization;
namespace Harbor.Tools.Mcp;

internal sealed class McpJsonRpcTransport : IAsyncDisposable
{
    private readonly JsonSerializerOptions _options;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private bool _disposed;

    public McpJsonRpcTransport(Stream input, Stream output, JsonSerializerOptions? options = null)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false), false, 8192, true);
        _writer = new StreamWriter(output, new UTF8Encoding(false), 8192, true) { AutoFlush = true };
        _options = options ?? new JsonSerializerOptions
        {
            TypeInfoResolver = McpJsonSerializerContext.Default,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _reader.Dispose();
        _writer.Dispose();
        return ValueTask.CompletedTask;
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
}
