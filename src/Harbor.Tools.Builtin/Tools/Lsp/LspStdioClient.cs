using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Lsp;

/// <summary>
///     One out-of-process language-server connection: JSON-RPC over stdio with
///     LSP <c>Content-Length</c> framing. Requests carry a per-request linked
///     CTS (timeout-aware, §FP-003/§FP-006 — no fire-and-forget), notifications
///     are dispatched to a caller callback, and the three standard server→client
///     requests (configuration, registerCapability, applyEdit) are answered
///     minimally so real servers never stall waiting on us.
/// </summary>
/// <remarks>
///     The transport is raw <see cref="Stream"/> pairs: when spawned from
///     <see cref="LspServerManager"/> the process owns them; tests inject
///     in-memory duplex streams with <c>process == null</c>.
/// </remarks>
internal sealed class LspStdioClient : IAsyncDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Process? _process;
    private readonly ILogger? _logger;
    private readonly Func<JsonElement, Task> _onNotification;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _loopCts = new();
    private Task? _readLoop;
    private int _nextId;
    private bool _disposed;

    public LspStdioClient(
        Stream input,
        Stream output,
        Process? process,
        Func<JsonElement, Task> onNotification,
        ILogger? logger = null)
    {
        _input = input;
        _output = output;
        _process = process;
        _onNotification = onNotification;
        _logger = logger;
    }

    /// <summary>Start the background read loop (idempotent).</summary>
    public void Start()
    {
        if (_readLoop is not null) return;
        _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
    }

    /// <summary>True once the read loop observed EOF (server died or stream closed).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>
    ///     Send a JSON-RPC request and await its response. Exactly one
    ///     <see cref="CancellationTokenSource"/> per call bounds the wait —
    ///     a stalled server can never wedge the caller (hard rule: no blocking
    ///     of the agent loop).
    /// </summary>
    /// <returns>The raw <c>result</c> element of the response.</returns>
    /// <exception cref="LspRpcException">Server returned a JSON-RPC error.</exception>
    /// <exception cref="OperationCanceledException">Timeout or caller cancellation.</exception>
    public async Task<JsonElement> SendRequestAsync(
        string method, object? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(timeout);
        using CancellationTokenRegistration registration = requestCts.Token.Register(
            static (state, token) => ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled(token),
            tcs);

        try
        {
            await WriteMessageAsync(BuildMessage(id, method, parameters), requestCts.Token).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        JsonElement result = await tcs.Task.ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out JsonElement error))
        {
            string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? "unknown LSP error"
                : "unknown LSP error";
            throw new LspRpcException(message);
        }

        return result.TryGetProperty("result", out JsonElement ok) ? ok.Clone() : default;
    }

    /// <summary>Fire a JSON-RPC notification (no id, no response).</summary>
    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await WriteMessageAsync(BuildMessage(id: null, method, parameters), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _loopCts.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* loop observes cancellation */ }
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _input.DisposeAsync().ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* already torn down with the process */ }
        finally
        {
            _writeLock.Release();
        }

        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                try { Harbor.Tools.Mcp.ProcessTree.KillTree(_process, null); }
                catch { /* best-effort teardown */ }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* proceed with dispose */ }
            }

            _process.Dispose();
        }

        _loopCts.Dispose();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Framing ────────────────────────────────────────────────────────────

    private string BuildMessage(int? id, string method, object? parameters)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, "2.0");
            if (id.HasValue)
            {
                writer.WriteNumber("id"u8, id.Value);
            }

            writer.WriteString("method"u8, method);
            switch (parameters)
            {
                case null:
                    break;
                case JsonElement el:
                    writer.WritePropertyName("params"u8);
                    el.WriteTo(writer);
                    break;
                case LspInitializeParams p:
                    writer.WritePropertyName("params"u8);
                    JsonSerializer.Serialize(writer, p, LspJsonContext.Default.LspInitializeParams);
                    break;
                case LspDidOpenParams p:
                    writer.WritePropertyName("params"u8);
                    JsonSerializer.Serialize(writer, p, LspJsonContext.Default.LspDidOpenParams);
                    break;
                case LspDidChangeParams p:
                    writer.WritePropertyName("params"u8);
                    JsonSerializer.Serialize(writer, p, LspJsonContext.Default.LspDidChangeParams);
                    break;
                case LspDidCloseParams p:
                    writer.WritePropertyName("params"u8);
                    JsonSerializer.Serialize(writer, p, LspJsonContext.Default.LspDidCloseParams);
                    break;
                case LspPositionParams p:
                    writer.WritePropertyName("params"u8);
                    JsonSerializer.Serialize(writer, p, LspJsonContext.Default.LspPositionParams);
                    break;
                case LspReferenceParams p:
                    writer.WritePropertyName("params"u8);
                    JsonSerializer.Serialize(writer, p, LspJsonContext.Default.LspReferenceParams);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"LSP parameter type {parameters.GetType().Name} is not registered in {nameof(LspJsonContext)}. " +
                        "Add a case to BuildMessage — reflection-based serialization is banned (§PERF-002).");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task WriteMessageAsync(string json, CancellationToken ct)
    {
        byte[] payload = StrictUtf8.GetBytes(json);
        byte[] header = StrictUtf8.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        byte[] frame = new byte[header.Length + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(frame, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Read one framed message: ASCII headers terminated by CRLFCRLF, then
    ///     an exact-length UTF-8 body. Returns null on EOF.
    /// </summary>
    private async Task<string?> ReadMessageAsync(CancellationToken ct)
    {
        byte[] chunk = new byte[8192];
        int buffered = 0, pos = 0;

        async ValueTask<int> NextByteAsync()
        {
            if (pos >= buffered)
            {
                buffered = await _input.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                pos = 0;
                if (buffered <= 0) return -1;
            }

            return chunk[pos++];
        }

        var header = new MemoryStream();
        const string terminator = "\r\n\r\n";
        int matched = 0;
        while (true)
        {
            int b = await NextByteAsync().ConfigureAwait(false);
            if (b < 0) return null;
            header.WriteByte((byte)b);
            if (b == terminator[matched])
            {
                matched++;
                if (matched == terminator.Length) break;
            }
            else
            {
                matched = b == terminator[0] ? 1 : 0;
            }

            if (header.Length > 32 * 1024)
            {
                throw new IOException("LSP header block exceeds 32KB — protocol desync.");
            }
        }

        int contentLength = ParseContentLength(Encoding.ASCII.GetString(header.ToArray()))
            ?? throw new IOException("LSP message missing Content-Length header.");

        if (contentLength > 64 * 1024 * 1024)
        {
            throw new IOException($"LSP message body of {contentLength} bytes exceeds the 64MB safety cap.");
        }

        byte[] body = new byte[contentLength];
        int total = 0;
        int leftover = Math.Min(buffered - pos, contentLength - total);
        Array.Copy(chunk, pos, body, total, leftover);
        pos += leftover;
        total += leftover;
        while (total < contentLength)
        {
            int n = await _input.ReadAsync(body.AsMemory(total), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            total += n;
        }

        return StrictUtf8.GetString(body);
    }

    private static int? ParseContentLength(string headerBlock)
    {
        foreach (string line in headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(line[(colon + 1)..].Trim(), out int len) && len >= 0 ? len : null;
        }

        return null;
    }

    // ── Dispatch ───────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? json = await ReadMessageAsync(ct).ConfigureAwait(false);
                if (json is null)
                {
                    break;
                }

                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                try
                {
                    if (root.TryGetProperty("id"u8, out JsonElement idEl))
                    {
                        if (root.TryGetProperty("method"u8, out JsonElement methodEl)
                            && methodEl.ValueKind == JsonValueKind.String)
                        {
                            await HandleServerRequestAsync(root, idEl, methodEl.GetString()!, ct).ConfigureAwait(false);
                        }
                        else if (idEl.ValueKind == JsonValueKind.Number
                                 && _pending.TryRemove(idEl.GetInt32(), out TaskCompletionSource<JsonElement>? tcs))
                        {
                            tcs.TrySetResult(root.Clone());
                        }

                        continue;
                    }

                    if (root.TryGetProperty("method"u8, out JsonElement notifyMethod)
                        && notifyMethod.ValueKind == JsonValueKind.String)
                    {
                        await _onNotification(root.Clone()).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to dispatch LSP message: {Json}", json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal teardown
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LSP read loop terminated");
        }
        finally
        {
            IsClosed = true;
            foreach (KeyValuePair<int, TaskCompletionSource<JsonElement>> kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out TaskCompletionSource<JsonElement>? tcs))
                {
                    tcs.TrySetException(new LspRpcException("Language server stream closed before responding."));
                }
            }
        }
    }

    private async Task HandleServerRequestAsync(JsonElement root, JsonElement idEl, string method, CancellationToken ct)
    {
        int id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
        switch (method)
        {
            case LspMethods.WorkspaceConfiguration:
                int itemCount = 1;
                if (root.TryGetProperty("params"u8, out JsonElement p)
                    && p.TryGetProperty("items"u8, out JsonElement items)
                    && items.ValueKind == JsonValueKind.Array)
                {
                    itemCount = Math.Max(items.GetArrayLength(), 1);
                }

                // No configuration data to share — one null per requested item
                // (spec-valid: servers fall back to their own defaults).
                var resultJson = new StringBuilder("[");
                for (int i = 0; i < itemCount; i++)
                {
                    if (i > 0) resultJson.Append(',');
                    resultJson.Append("null");
                }

                resultJson.Append(']');
                await RespondAsync(id, resultJson.ToString(), ct).ConfigureAwait(false);
                break;

            case LspMethods.RegisterCapability:
            case LspMethods.LogMessage:
            case LspMethods.ShowMessage:
                // registerCapability → acknowledge; window/* requests stay logged-only.
                await RespondAsync(id, "null", ct).ConfigureAwait(false);
                break;

            case LspMethods.ApplyEdit:
                await RespondAsync(id, "{\"applied\":false,\"reason\":\"Harbor LSP client does not apply workspace edits.\"}", ct)
                    .ConfigureAwait(false);
                break;

            default:
                _logger?.LogDebug("LSP server request '{Method}' → method-not-found response", method);
                await WriteRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32601,\"message\":\"Method not found: {method}\"}}}}", ct)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task RespondAsync(int id, string resultJson, CancellationToken ct)
    {
        await WriteRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}", ct).ConfigureAwait(false);
    }

    private Task WriteRawAsync(string json, CancellationToken ct) => WriteMessageAsync(json, ct);
}

/// <summary>JSON-RPC error returned by a language server.</summary>
public sealed class LspRpcException : Exception
{
    public LspRpcException()
        : this("LSP request failed.")
    {
    }

    public LspRpcException(string message) : base(message)
    {
    }

    public LspRpcException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
