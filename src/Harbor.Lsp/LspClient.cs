using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
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
///     <para>
///         <b>Errors (§C7):</b> framing throws (<c>EndOfStreamException</c>,
///         <c>FormatException</c>) stay inside as the session-abort mechanism — the read loop
///         logs and raises <see cref="Disconnected"/>. The <c>LspManager</c> boundary maps every
///         such failure to a degrade-with-reason log, so the cause survives instead of vanishing.
///     </para>
/// </remarks>
public sealed class LspClient : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // #1095: pending-request demux is on the LSP hot path — SendRequestAsync
    // registers/unregisters per request, the reader loop looks up per response,
    // Dispose cancels on shutdown. A ConcurrentDictionary keeps all four sites
    // lock-free; ids are unique via Interlocked, TCS ops are thread-safe, and
    // Dispose enumeration is a moment-in-time snapshot. Same discipline as
    // DebouncedPluginWatcher._pending.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly byte[] _oneByte = new byte[1]; // reused header probe — the read loop is the only reader
    private readonly byte[] _headerScratch = new byte[32]; // formatted + written under _writeLock, never shared
    private Task? _readLoopTask;
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

    /// <summary>
    ///     Starts the background read loop. The loop task is held (never fire-and-forget, §D1)
    ///     with an <c>OnlyOnFaulted</c> continuation so a fault — e.g. a throwing
    ///     <see cref="Disconnected"/> subscriber — is always logged.
    /// </summary>
    public void Start()
    {
        _readLoopTask = ReadLoopAsync(_lifetimeCts.Token);
        _ = _readLoopTask.ContinueWith(
            static (t, state) => LspClientLog.ReadLoopFaulted((ILogger)state!, t.Exception!),
            _logger,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>
    ///     Sends a request and awaits the server's result. The returned
    ///     <see cref="JsonElement"/> is a clone — valid after the frame is freed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The pending entry is registered BEFORE the frame goes out</b>
    ///         (#1885). The frame is on the pipe as soon as the write returns,
    ///         and the reader loop can pick the answer up before this method
    ///         reaches the line that registers the waiter — the answer is
    ///         delivered while the writer is still inside
    ///         <see cref="WriteFrameAsync" />. Registering afterwards dropped
    ///         that answer on the floor: the reader found no entry for the id
    ///         and discarded the frame, and the requester then waited for a
    ///         reply that had already been thrown away. The wait was unbounded,
    ///         so a lost answer was a HUNG session rather than a failed one.
    ///     </para>
    ///     <para>
    ///         The window is narrow in a warm process and wide in a slow one
    ///         (instrumented, contended, cold), which is why this read as a
    ///         flake and not as a break. <c>MessagePackRpcClient</c> in the IPC
    ///         client registers its waiter in the same order and keeps the same
    ///         "the request never made it to the wire" cleanup; this method
    ///         now matches it.
    ///     </para>
    /// </remarks>
    public async Task<JsonElement?> SendRequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register FIRST: the answer can arrive before the write returns.
        _pending[id] = tcs;

        try
        {
            await WriteFrameAsync(BuildFrame("2.0", id, method, parameters), ct).ConfigureAwait(false);
        }
        catch
        {
            // The request never made it to the wire — no answer can arrive, so
            // the entry must not linger (same discipline as MessagePackRpcClient).
            _ = _pending.TryRemove(id, out _);
            throw;
        }

        try
        {
            return (await tcs.Task.WaitAsync(ct).ConfigureAwait(false)).Clone();
        }
        finally
        {
            _ = _pending.TryRemove(id, out _);
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
        foreach (var tcs in _pending.Values)
        {
            _ = tcs.TrySetCanceled();
        }

        _pending.Clear();

        _lifetimeCts.Dispose();
        _writeLock.Dispose();
    }

    // ── Framing ────────────────────────────────────────────────────────────

    private static ArrayBufferWriter<byte> BuildFrame(string jsonrpc, int? id, string method, object? parameters)
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

        return buffer;
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

    private async Task WriteFrameAsync(ArrayBufferWriter<byte> payload, CancellationToken ct)
    {
        // #180: frame the pooled UTF-8 bytes directly — no string payload,
        // no re-encode per frame. The header is formatted into scratch space
        // under the write lock (the only writer), then both parts stream out.
        // "Content-Length: " (16) + up to 10 digits + "\r\n\r\n" (4) fits 32 bytes.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            const int PrefixLength = 16;
            "Content-Length: "u8.CopyTo(_headerScratch);
            if (!payload.WrittenCount.TryFormat(_headerScratch.AsSpan(PrefixLength), out int digits))
                throw new InvalidOperationException("LSP frame is too large to frame.");
            _headerScratch[PrefixLength + digits] = (byte)'\r';
            _headerScratch[PrefixLength + digits + 1] = (byte)'\n';
            _headerScratch[PrefixLength + digits + 2] = (byte)'\r';
            _headerScratch[PrefixLength + digits + 3] = (byte)'\n';

            await _output.WriteAsync(_headerScratch.AsMemory(0, PrefixLength + digits + 4), ct).ConfigureAwait(false);
            await _output.WriteAsync(payload.WrittenMemory, ct).ConfigureAwait(false);
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
                JsonElement? root = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (root is null) break;

                HandleFrame(root.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LspClientLog.ReadLoopEnded(_logger, ex);
        }

        LspClientLog.ReadLoopExited(_logger);
        RaiseDisconnected();
    }

    /// <summary>
    ///     Dispatch a server notification to every subscriber, one at a time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A subscriber that throws must not take the READ LOOP down with
    ///         it. The loop that reads the child's stdout is the only thing
    ///         keeping that child from blocking on a full pipe, so a fault in a
    ///         notification handler is contained here: the loop keeps draining,
    ///         the other subscribers still run, and the fault is logged.
    ///     </para>
    ///     <para>
    ///         The same isolation applies to <see cref="Disconnected" />: a
    ///         throwing subscriber there would otherwise stop the subscribers
    ///         after it from running — including the one that kills a child
    ///         whose pipe is no longer being drained.
    ///     </para>
    /// </remarks>
    private void RaiseServerNotification(LspNotificationEventArgs args)
    {
        EventHandler<LspNotificationEventArgs>? handlers = ServerNotification;
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<LspNotificationEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                LspClientLog.SubscriberFaulted(_logger, ex);
            }
        }
    }

    private void RaiseDisconnected()
    {
        EventHandler? handlers = Disconnected;
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LspClientLog.SubscriberFaulted(_logger, ex);
            }
        }
    }

    private void HandleFrame(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("method", out JsonElement methodEl) && methodEl.ValueKind == JsonValueKind.String)
        {
            JsonElement paramsElement = root.TryGetProperty("params", out JsonElement p) ? p.Clone() : default;
            RaiseServerNotification(new LspNotificationEventArgs(methodEl.GetString()!, paramsElement));
            return;
        }

        if (root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number)
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

    /// <summary>ASCII whitespace trim for header spans (no byte-span Trim() in BCL).</summary>
    private static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t'))
            start++;
        int end = span.Length;
        while (end > start && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
            end--;
        return span[start..end];
    }

    private async Task<JsonElement?> ReadFrameAsync(CancellationToken ct)
    {
        int contentLength = await ReadHeadersAsync(ct).ConfigureAwait(false);
        byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await ReadExactlyAsync(body, contentLength, ct).ConfigureAwait(false);
            // Same DOM as JsonDocument.Parse, but through the source-generated
            // JsonElement info (PERF-002): the element is self-contained, so
            // no document lifetime is tracked by the read loop. Malformed
            // bodies still throw JsonException — the loop logs and aborts the
            // session exactly as before.
            return JsonSerializer.Deserialize(body.AsSpan(0, contentLength), LspJsonContext.Default.JsonElement);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private async ValueTask<int> ReadHeadersAsync(CancellationToken ct)
    {
        // Header blocks are tiny (~40 bytes): pooled buffer, byte-wise scan
        // for the \r\n\r\n terminator, Content-Length parsed from the span —
        // no List<byte>, no header string, no Split (#180).
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        int count = 0;
        try
        {
            while (true)
            {
                int b = await ReadByteAsync(ct).ConfigureAwait(false);
                if (b < 0) throw new EndOfStreamException("LSP stream ended while reading headers");

                if (count >= rented.Length)
                {
                    if (rented.Length >= 16_384)
                        throw new InvalidOperationException("LSP header block exceeded 16 KB.");
                    byte[] grown = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
                    Buffer.BlockCopy(rented, 0, grown, 0, count);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = grown;
                }

                rented[count++] = (byte)b;
                if (count >= 4
                    && rented[count - 4] == (byte)'\r' && rented[count - 3] == (byte)'\n'
                    && rented[count - 2] == (byte)'\r' && rented[count - 1] == (byte)'\n')
                {
                    break;
                }

                if (count > 16_384)
                {
                    throw new InvalidOperationException("LSP header block exceeded 16 KB.");
                }
            }

            return ParseContentLength(rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Find the <c>Content-Length</c> line (case-insensitive, like the old
    /// string Split version) and parse its value from the span. An
    /// unparseable value is ignored in favor of later lines, exactly as
    /// <c>int.TryParse</c> failing did before.
    /// </summary>
    private static int ParseContentLength(ReadOnlySpan<byte> headers)
    {
        while (!headers.IsEmpty)
        {
            int eol = headers.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = eol < 0 ? headers : headers[..eol];
            headers = eol < 0 ? default : headers[(eol + 2)..];

            if (line.Length > "Content-Length:".Length
                && Ascii.EqualsIgnoreCase(line[.."Content-Length:".Length], "Content-Length:"u8))
            {
                // int.TryParse accepted an explicit '+' sign; Utf8Parser may
                // not, so strip it after the whitespace trim it also needed.
                ReadOnlySpan<byte> value = TrimAsciiWhiteSpace(line["Content-Length:".Length..]);
                if (value.StartsWith("+"u8))
                    value = value[1..];
                if (Utf8Parser.TryParse(value, out int length, out int consumed) && consumed == value.Length)
                    return length;
            }
        }

        throw new FormatException("LSP frame is missing a Content-Length header.");
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        int read = await _input.ReadAsync(_oneByte.AsMemory(), ct).ConfigureAwait(false);
        return read == 0 ? -1 : _oneByte[0];
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

/// <summary>
///     SG13: BCL <c>[LoggerMessage]</c> delegates for <see cref="LspClient" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class LspClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "LSP read loop faulted")]
    public static partial void ReadLoopFaulted(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "LSP read loop ended")]
    public static partial void ReadLoopEnded(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "LSP read loop exited")]
    public static partial void ReadLoopExited(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "LSP event subscriber faulted; contained, the read loop keeps draining")]
    public static partial void SubscriberFaulted(ILogger logger, Exception ex);
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
