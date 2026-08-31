using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Harbor.Lsp;

/// <summary>
///     JSON-RPC over the LSP wire format (Content-Length framed) on a pair of
///     <see cref="Stream"/>s — one running language server connection.
/// </summary>
/// <remarks>
///     <para>
///         <b>Framing:</b> <c>Content-Length: N\r\n\r\n</c> + N UTF-8 bytes of
///         one JSON object — the LSP/JSON-RPC base protocol, not NDJSON.
///     </para>
///     <para>
///         <b>Demux:</b> the reader loop routes responses by <c>id</c> to
///         pending requests and surfaces server notifications
///         (<c>textDocument/publishDiagnostics</c>, …) through
///         <see cref="ServerNotification" />.
///     </para>
///     <para>
///         <b>AOT:</b> params serialize through <see cref="LspJsonContext"/>
///         source generation; responses are read as raw
///         <see cref="JsonElement"/>s and normalized by callers.
///     </para>
/// </remarks>
public sealed class LspClient : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Lock _pendingLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _nextId;
    private int _disposed;

    /// <summary>Raised for every server→client notification (method, params).</summary>
    public event EventHandler<LspNotificationEventArgs>? ServerNotification;

    /// <summary>Raised when the read loop ends (process exited / stream closed).</summary>
    public event EventHandler? Disconnected;

    public LspClient(Stream input, Stream output, ILogger logger)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts the background read loop.</summary>
    public void Start() => _ = ReadLoopAsync(_lifetimeCts.Token);

    /// <summary>
    ///     Sends a request and awaits the server's result. The returned
    ///     <see cref="JsonElement"/> is a clone — valid after the frame is freed.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int id = Interlocked.Increment(ref _nextId);
        await WriteFrameAsync(BuildFrame("2.0", id, method, parameters), ct).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        try
        {
            return (await tcs.Task.WaitAsync(ct).ConfigureAwait(false)).Clone();
        }
        finally
        {
            lock (_pendingLock)
            {
                _ = _pending.Remove(id);
            }
        }
    }

    /// <summary>Sends a notification (no response expected).</summary>
    public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct = default)
        => WriteFrameAsync(BuildFrame("2.0", null, method, parameters), ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        lock (_pendingLock)
        {
            foreach (var tcs in _pending.Values)
            {
                _ = tcs.TrySetCanceled();
            }

            _pending.Clear();
        }

        _lifetimeCts.Dispose();
        _writeLock.Dispose();
    }

    // ── Framing ────────────────────────────────────────────────────────────

    private static string BuildFrame(string jsonrpc, int? id, string method, object? parameters)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", jsonrpc);
            if (id is { } requestId)
            {
                json.WriteNumber("id", requestId);
            }

            json.WriteString("method", method);
            if (parameters is not null)
            {
                json.WritePropertyName("params");
                json.WriteRawValue(JsonSerializeParams(parameters), skipInputValidation: false);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Source-generated serialization for known param DTOs; raw JsonElement passes through.</summary>
    private static string JsonSerializeParams(object parameters)
    {
        return parameters switch
        {
            JsonElement element => element.GetRawText(),
            LspWire.InitializeParams p => JsonSerializer.Serialize(p, LspJsonContext.Default.InitializeParams),
            LspWire.DidOpenTextDocumentParams p => JsonSerializer.Serialize(p, LspJsonContext.Default.DidOpenTextDocumentParams),
            LspWire.DidChangeTextDocumentParams p => JsonSerializer.Serialize(p, LspJsonContext.Default.DidChangeTextDocumentParams),
            LspWire.DidCloseTextDocumentParams p => JsonSerializer.Serialize(p, LspJsonContext.Default.DidCloseTextDocumentParams),
            LspWire.PositionParams p => JsonSerializer.Serialize(p, LspJsonContext.Default.PositionParams),
            _ => throw new InvalidOperationException($"No source-generated serializer for {parameters.GetType().Name}."),
        };
    }

    private async Task WriteFrameAsync(string payload, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        string header = $"Content-Length: {body.Length}\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await _output.WriteAsync(body, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonDocument? doc = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (doc is null) break;

                using (doc)
                {
                    HandleFrame(doc.RootElement);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP read loop ended");
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void HandleFrame(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("method", out JsonElement methodEl) && methodEl.ValueKind == JsonValueKind.String)
        {
            JsonElement paramsElement = root.TryGetProperty("params", out JsonElement p) ? p.Clone() : default;
            ServerNotification?.Invoke(this, new LspNotificationEventArgs(methodEl.GetString()!, paramsElement));
            return;
        }

        if (root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            lock (_pendingLock)
            {
                if (_pending.TryGetValue(idEl.GetInt32(), out var tcs))
                {
                    if (root.TryGetProperty("result", out JsonElement result))
                    {
                        _ = tcs.TrySetResult(result.Clone());
                    }
                    else if (root.TryGetProperty("error", out JsonElement error))
                    {
                        string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString()!
                            : "LSP request failed";
                        _ = tcs.TrySetException(new LspRequestException(message));
                    }
                    else
                    {
                        _ = tcs.TrySetResult(default);
                    }
                }
            }
        }
    }

    private async Task<JsonDocument?> ReadFrameAsync(CancellationToken ct)
    {
        int contentLength = await ReadHeadersAsync(ct).ConfigureAwait(false);
        byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await ReadExactlyAsync(body, contentLength, ct).ConfigureAwait(false);
            return JsonDocument.Parse(body.AsMemory(0, contentLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private async ValueTask<int> ReadHeadersAsync(CancellationToken ct)
    {
        // Header blocks are tiny (~40 bytes); byte-wise scanning is simple and
        // correct, the body below is read in bulk.
        var bytes = new List<byte>(64);
        while (true)
        {
            int b = await ReadByteAsync(ct).ConfigureAwait(false);
            if (b < 0) throw new EndOfStreamException("LSP stream ended while reading headers");

            bytes.Add((byte)b);
            if (bytes.Count >= 4
                && bytes[^4] == (byte)'\r' && bytes[^3] == (byte)'\n'
                && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
            {
                break;
            }

            if (bytes.Count > 16_384)
            {
                throw new InvalidOperationException("LSP header block exceeded 16 KB.");
            }
        }

        string header = Encoding.ASCII.GetString(bytes.ToArray());
        foreach (string line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out int length))
            {
                return length;
            }
        }

        throw new FormatException("LSP frame is missing a Content-Length header.");
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        var one = new byte[1];
        int read = await _input.ReadAsync(one, ct).ConfigureAwait(false);
        return read == 0 ? -1 : one[0];
    }

    private async ValueTask ReadExactlyAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("LSP stream ended mid-frame");
            total += read;
        }
    }
}

/// <summary>The language server answered a request with a JSON-RPC error.</summary>
public sealed class LspRequestException : Exception
{
    /// <summary>Create with the default message.</summary>
    public LspRequestException() : this("LSP request failed.")
    {
    }

    /// <summary>Create with a message.</summary>
    public LspRequestException(string message) : base(message)
    {
    }

    /// <summary>Create with a message and inner exception.</summary>
    public LspRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Payload of <see cref="LspClient.ServerNotification" />.</summary>
public sealed class LspNotificationEventArgs(string method, JsonElement parameters) : EventArgs
{
    /// <summary>The notification method (e.g. <c>textDocument/publishDiagnostics</c>).</summary>
    public string Method { get; } = method;

    /// <summary>The cloned <c>params</c> element (default when absent).</summary>
    public JsonElement Parameters { get; } = parameters;
}
