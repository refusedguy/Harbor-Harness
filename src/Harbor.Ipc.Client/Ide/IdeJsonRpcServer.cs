using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Harbor.Ipc.Ide;

/// <summary>
///     A single bridge request handler. Returns the JSON payload for the
///     <c>result</c> member (pre-serialized through <see cref="IdeJsonContext" />)
///     or <see langword="null"/> for an empty result. Typed failures throw
///     <see cref="IdeRpcException" />; any other exception becomes
///     code <see cref="IdeRpcException.HandlerError" />.
/// </summary>
public delegate Task<JsonElement?> IdeRequestHandler(
    string method,
    JsonElement? parameters,
    CancellationToken requestCt);

/// <summary>Tunables for <see cref="IdeJsonRpcServer" />.</summary>
public sealed record IdeJsonRpcServerOptions
{
    /// <summary>
    ///     Per-request cancellation budget — every request gets its own CTS
    ///     linked to the server lifetime and cancelled when this expires.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
///     JSON-RPC 2.0 framing server over newline-delimited JSON (NDJSON) —
///     one object per line on the editor-facing stdio streams of the
///     <c>harbor ide</c> bridge.
/// </summary>
/// <remarks>
///     <para>
///         <b>Concurrency:</b> the read loop never awaits a handler — each
///         request is dispatched concurrently and its response (or error)
///         written under the write lock when it completes, so a slow
///         <c>inject_prompt</c> never blocks the editor's next request.
///     </para>
///     <para>
///         <b>Per-request CTS:</b> each dispatched request runs under a CTS
///         linked to the server lifetime and cancelled on
///         <see cref="IdeJsonRpcServerOptions.RequestTimeout" /> expiry —
///         the bridge never leaves a handler running unbounded.
///     </para>
///     <para>
///         <b>Framing:</b> envelopes are <c>{"jsonrpc":"2.0","id":…,"method":…, "params":…}</c>.
///         Objects without an <c>id</c> are notifications from the editor and
///         are acknowledged by silence. Responses embed the handler's
///         pre-serialized payload via <c>WriteRawValue</c> — no re-serialization
///         or reflection (§PERF-002).
///     </para>
/// </remarks>
public sealed class IdeJsonRpcServer : IAsyncDisposable
{
    private readonly IdeRequestHandler _handler;
    private readonly ILogger _logger;
    private readonly IdeJsonRpcServerOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TextWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _inFlight = [];
    private readonly Lock _inFlightLock = new();
    private int _disposed;

    /// <summary>
    ///     Construct a bridge server over the editor-facing streams.
    ///     The writer is flushed after every line (stdio requires it —
    ///     editors read incrementally).
    /// </summary>
    public IdeJsonRpcServer(
        TextReader input,
        TextWriter output,
        IdeRequestHandler handler,
        ILogger logger,
        IdeJsonRpcServerOptions? options = null)
    {
        Input = input;
        _writer = output;
        _handler = handler;
        _logger = logger;
        _options = options ?? new IdeJsonRpcServerOptions();
    }

    /// <summary>The editor-facing input stream (stdin of the bridge process).</summary>
    public TextReader Input { get; }

    /// <summary>Raised when the editor closes stdin (EOF) — the bridge host uses it to shut down.</summary>
    public event EventHandler StdioClosed = delegate { };

    /// <summary>
    ///     Serve requests until the editor closes stdin or the server is
    ///     cancelled/disposed. Returns when the read loop ends; in-flight
    ///     requests are cancelled by <see cref="DisposeAsync" />.
    /// </summary>
    public async Task RunAsync(CancellationToken serverCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, serverCt);
        while (!linked.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Input.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IDE bridge stdin read failed — closing");
                break;
            }

            if (line is null)
            {
                _logger.LogDebug("IDE bridge stdin closed by editor (EOF)");
                StdioClosed.Invoke(this, EventArgs.Empty);
                break;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            await HandleLine(line, linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Push a server→editor notification (e.g. <c>stream</c> deltas).
    ///     Serialized against responses via the write lock so lines never
    ///     interleave.
    /// </summary>
    public async Task WriteNotificationAsync(string method, JsonElement? payload, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("method");
            json.WriteStringValue(method);
            if (payload is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } p)
            {
                json.WritePropertyName("params");
                json.WriteRawValue(p.GetRawText(), skipInputValidation: false);
            }
            json.WriteEndObject();
        }

        string line = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync().ConfigureAwait(false);

        // Short grace window: in-flight handlers observe the cancel and
        // DisposeAsync never hangs on a stuck handler.
        Task[] tasks;
        lock (_inFlightLock)
        {
            tasks = [.. _inFlight];
            _inFlight.Clear();
        }

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IDE bridge in-flight requests did not drain within the grace window");
            }
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }

    // ── Request routing ────────────────────────────────────────────────────

    private async Task HandleLine(string line, CancellationToken serverCt)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "IDE bridge received malformed JSON line");
            // No id available — respond with a null id so the editor's
            // demux surfaces the parse failure instead of hanging.
            await WriteErrorAsync(default, IdeRpcException.InvalidRequest, ex.Message).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(default, IdeRpcException.InvalidRequest, "Request must be a JSON object.")
                    .ConfigureAwait(false);
                return;
            }

            // Notifications (no id) from the editor are accepted and ignored.
            if (!root.TryGetProperty("id", out JsonElement id))
            {
                _logger.LogDebug("IDE bridge ignored editor notification: {Line}", line);
                return;
            }

            if (!root.TryGetProperty("method", out JsonElement methodEl) || methodEl.ValueKind != JsonValueKind.String)
            {
                await WriteErrorAsync(id, IdeRpcException.InvalidRequest, "Missing string 'method'.")
                    .ConfigureAwait(false);
                return;
            }

            string method = methodEl.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out JsonElement p) ? p : null;

            Dispatch(method, parameters, id, serverCt);
        }
    }

    private void Dispatch(string method, JsonElement? parameters, JsonElement id, CancellationToken serverCt)
    {
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, serverCt);
        requestCts.CancelAfter(_options.RequestTimeout);

        var task = Task.Run(async () =>
        {
            using (requestCts)
            {
                try
                {
                    JsonElement? result = await _handler(method, parameters, requestCts.Token)
                        .ConfigureAwait(false);
                    await WriteResultAsync(id, result, requestCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    await WriteErrorAsync(id, -32002, $"Request '{method}' timed out after {_options.RequestTimeout.TotalSeconds:F0}s.")
                        .ConfigureAwait(false);
                }
                catch (IdeRpcException ex)
                {
                    await WriteErrorAsync(id, ex.Code, ex.Message).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Server shutdown — the editor will see the closed stdio.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IDE bridge handler '{Method}' failed", method);
                    await WriteErrorAsync(id, IdeRpcException.HandlerError, ex.Message).ConfigureAwait(false);
                }
            }
        });

        lock (_inFlightLock)
        {
            _inFlight.RemoveAll(static t => t.IsCompleted);
            _inFlight.Add(task);
        }
    }

    // ── Response writing ───────────────────────────────────────────────────

    private async Task WriteResultAsync(JsonElement id, JsonElement? result, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            WriteEnvelopeStart(json, id, "result");
            if (result is { ValueKind: not JsonValueKind.Undefined } r)
            {
                json.WriteRawValue(r.GetRawText(), skipInputValidation: false);
            }
            else
            {
                json.WriteNullValue();
            }
            json.WriteEndObject();
        }

        await WriteLineAsync(buffer, ct).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(JsonElement id, int code, string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            WriteEnvelopeStart(json, id, "error");
            json.WriteStartObject("error");
            json.WriteNumber("code", code);
            json.WriteString("message", message);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        await WriteLineAsync(buffer, CancellationToken.None).ConfigureAwait(false);
    }

    private static void WriteEnvelopeStart(Utf8JsonWriter json, JsonElement id, string memberName)
    {
        json.WriteStartObject();
        json.WriteString("jsonrpc", "2.0");
        json.WritePropertyName("id");
        if (id.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array)
        {
            // Malformed/absent id — echo JSON null so the envelope stays valid.
            json.WriteNullValue();
        }
        else
        {
            json.WriteRawValue(id.GetRawText(), skipInputValidation: false);
        }
        json.WritePropertyName(memberName);
    }

    private async Task WriteLineAsync(ArrayBufferWriter<byte> buffer, CancellationToken ct)
    {
        string line = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
