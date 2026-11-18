using System.Buffers;
using System.Collections.Concurrent;
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
///         one JSON object — the LSP/JSON-RPC base protocol, not NDJSON. The
///         byte-level framing lives in <see cref="ILspFrameTransport" />; this
///         type only builds the JSON-RPC envelope and drives that transport.
///     </para>
///     <para>
///         <b>Demux:</b> the reader loop routes responses by <c>id</c> to
///         pending requests and surfaces server notifications
///         (<c>textDocument/publishDiagnostics</c>, …) through
///         <see cref="ServerNotification" />. Correlation stays here; the
///         transport knows nothing about request ids.
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
    private readonly ILspFrameTransport _transport;
    private readonly ILogger _logger;
    // #1095: pending-request demux is on the LSP hot path — SendRequestAsync
    // registers/unregisters per request, the reader loop looks up per response,
    // Dispose cancels on shutdown. A ConcurrentDictionary keeps all four sites
    // lock-free; ids are unique via Interlocked, TCS ops are thread-safe, and
    // Dispose enumeration is a moment-in-time snapshot. Same discipline as
    // DebouncedPluginWatcher._pending.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _readLoopTask;
    private int _nextId;
    private int _disposed;

    /// <summary>Raised for every server→client notification (method, params).</summary>
    public event EventHandler<LspNotificationEventArgs>? ServerNotification;

    /// <summary>Raised when the read loop ends (process exited / stream closed).</summary>
    public event EventHandler? Disconnected;

    /// <summary>Convenience constructor for the wire's two streams.</summary>
    public LspClient(Stream input, Stream output, ILogger logger)
        : this(new LspFrameTransport(input, output), logger)
    {
    }

    /// <summary>
    ///     Inject a transport (#2613). The framing seam is what lets the
    ///     client be tested without a process: a fake feeds frames in and
    ///     records the envelopes written out, while demux stays here.
    /// </summary>
    internal LspClient(ILspFrameTransport transport, ILogger logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
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
    ///         <see cref="ILspFrameTransport.WriteFrameAsync" />. Registering afterwards dropped
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
            await _transport.WriteFrameAsync(BuildFrame("2.0", id, method, parameters).WrittenMemory, ct).ConfigureAwait(false);
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
        => _transport.WriteFrameAsync(BuildFrame("2.0", null, method, parameters).WrittenMemory, ct);

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
        await _transport.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>
    ///     Serializes a param object through the AOT-safe source-generated
    ///     <see cref="LspJsonContext"/>. The concrete type is resolved at run time
    ///     through the context, so adding a new LSP param DTO needs only a
    ///     <c>[JsonSerializable]</c> registration there. This client never grows
    ///     another switch arm (OCP). A raw <see cref="JsonElement"/> passes through
    ///     byte for byte, preserving the caller's original JSON.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The param type is not registered in <see cref="LspJsonContext"/>.
    /// </exception>
    private static string JsonSerializeParams(object parameters)
    {
        if (parameters is JsonElement element)
            return element.GetRawText();

        Type type = parameters.GetType();
        return JsonSerializer.Serialize(
            parameters,
            LspJsonContext.Default.GetTypeInfo(type)
            ?? throw new InvalidOperationException(
                $"LSP param type {type.Name} is not registered in {nameof(LspJsonContext)}. "
                + "Add a [JsonSerializable] for it. An unregistered type cannot be "
                + "serialized without reflection."));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonElement? root = await _transport.ReadFrameAsync(ct).ConfigureAwait(false);
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
