namespace Harbor.E2E.Framework;
/// <summary>
///     In-process HTTP server that emulates an OpenAI-compatible LLM provider
///     for E2E tests. Returns canned chat-completion responses (SSE-streamed)
///     and a static model list. Thread-safe: multiple concurrent requests are
///     served independently from a single <see cref="HttpListener" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Wire shape:</b>
///         <list type="bullet">
///             <item>
///                 <c>POST {BaseUri}/chat/completions</c> — OpenAI streaming
///                 chat-completions endpoint. Returns <c>text/event-stream</c>
///                 with one <c>data: {...}\n\n</c> line per token, ending with
///                 <c>data: [DONE]\n\n</c>.
///             </item>
///             <item>
///                 <c>POST {BaseUri}/v1/chat/completions</c> — same, for the
///                 <c>/v1</c>-prefixed form (some clients prepend it).
///             </item>
///             <item>
///                 <c>GET {BaseUri}/models</c> — static OpenAI-shaped
///                 <c>{ "object": "list", "data": [...] }</c> response.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Threading:</b> <see cref="HttpListener.GetContextAsync" /> is
///         awaited on the listener loop; each accepted request is dispatched to
///         the thread pool via <c>Task.Run</c> so the loop continues. The
///         <see cref="ReceivedRequests" /> list is guarded by a lock; reads from
///         test code are safe.
///     </para>
///     <para>
///         <b>Response selection:</b> a per-model response is configured via
///         <see cref="SetResponse" />. If no response is configured for the
///         requested model, the server returns a single-token fallback so the
///         test fails loudly rather than hanging.
///     </para>
/// </remarks>
public sealed class MockLlmServer : IAsyncDisposable
{
    private readonly List<ChatCompletionRequest> _received = new();
    private readonly object _receivedLock = new();
    private readonly Dictionary<string, CannedResponse> _responses = new(StringComparer.Ordinal);
    private readonly object _responsesLock = new();
    private CancellationTokenSource? _cts;
    private HttpListener _listener = new();
    private Task? _loopTask;
    private int _requestCount;

    /// <summary>
    ///     The base URI clients should target. Populated after <see cref="StartAsync" />.
    ///     Always <c>http://localhost:&lt;port&gt;</c> with no trailing slash — the
    ///     Harbor OpenAI-compatible client appends <c>/chat/completions</c>.
    /// </summary>
    public Uri BaseUri { get; private set; } = new("http://localhost:0");

    /// <summary>
    ///     Snapshot of every chat-completion request the server has received, in
    ///     arrival order. Useful for tests asserting the agent sent the right
    ///     system prompt / model / tool list.
    /// </summary>
    public IReadOnlyList<ChatCompletionRequest> ReceivedRequests
    {
        get
        {
            lock (_receivedLock)
            {
                return _received.ToList();
            }
        }
    }

    /// <summary>Total number of requests served (any endpoint). For diagnostics.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    /// <summary>
    ///     Start listening on a random localhost port. Returns the resolved
    ///     <see cref="BaseUri" />. Idempotent — calling twice is a no-op.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loopTask is not null)
            return Task.CompletedTask;

        // HttpListener on .NET 10 supports wildcarding the port via "+" — but
        // most CI sandboxes block "+" prefixes (admin required). Use a fixed
        // high-range port and retry a few times if it's in use.
        _cts = new CancellationTokenSource();
        Exception? lastError = null;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            int port = Random.Shared.Next(50_000, 60_000);
            string prefix = FormattableString.Invariant($"http://localhost:{port}/");
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                BaseUri = new Uri(FormattableString.Invariant($"http://localhost:{port}"));
                _loopTask = Task.Run(() => ListenerLoop(_cts.Token), _cts.Token);
                return Task.CompletedTask;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                try { _listener.Stop(); }
                catch
                { /* ignore */
                }
                _listener = new HttpListener();
            }
        }
        throw new InvalidOperationException("Could not bind MockLlmServer to any port.", lastError);
    }

    /// <summary>
    ///     Stop the listener and abandon in-flight requests. Safe to call from
    ///     <see cref="IAsyncDisposable.DisposeAsync" />.
    /// </summary>
    public Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is null)
            return Task.CompletedTask;
        _cts.Cancel();
        try { _listener.Stop(); }
        catch
        { /* ignore */
        }
        try { _listener.Close(); }
        catch
        { /* ignore */
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Configure the canned response for a given model id. Subsequent
    ///     <c>chat/completions</c> requests for that model will stream the
    ///     <paramref name="responseText" /> token-by-token.
    /// </summary>
    public void SetResponse(string model, string responseText)
    {
        var canned = new CannedResponse(responseText, false, null, null);
        lock (_responsesLock)
        {
            _responses[model] = canned;
        }
    }

    /// <summary>
    ///     Configure a canned tool-call response: instead of text, the server
    ///     will emit a single tool-call chunk requesting <paramref name="toolName" />
    ///     with the given JSON-encoded <paramref name="args" />.
    /// </summary>
    public void SetToolCallResponse(string model, string toolName, object args)
    {
        string argsJson = JsonSerializer.Serialize(args);
        var canned = new CannedResponse(null, true, toolName, argsJson);
        lock (_responsesLock)
        {
            _responses[model] = canned;
        }
    }

    private async Task ListenerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // Listener was stopped.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Dispatch each request to the thread pool so the listener loop
            // stays responsive. Each handler owns its own ctx and disposes it.
            _ = Task.Run(() => HandleAsync(ctx, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _requestCount);
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (req.HttpMethod == "GET" && (path == "/models" || path == "/v1/models"))
            {
                await WriteModelsAsync(resp, ct).ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "POST" && (path == "/chat/completions" || path == "/v1/chat/completions"))
            {
                await WriteChatCompletionAsync(req, resp, ct).ConfigureAwait(false);
                return;
            }

            resp.StatusCode = 404;
            byte[] body = Encoding.UTF8.GetBytes("not found");
            resp.ContentType = "text/plain";
            resp.ContentLength64 = body.Length;
            await resp.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try
            {
                resp.StatusCode = 500;
                byte[] body = Encoding.UTF8.GetBytes("mock error: " + ex.Message);
                resp.ContentLength64 = body.Length;
                await resp.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            catch
            { /* swallow secondary */
            }
        }
        finally
        {
            try { resp.Close(); }
            catch
            { /* ignore */
            }
        }
    }

    private static async Task WriteModelsAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        // Minimal OpenAI-shaped models list. Tests don't depend on the exact
        // contents — only that the request succeeds.
        var payload = new
        {
            @object = "list",
            data = new object[]
            {
                new { id = "mock/test-model", @object = "model", created = 0, owned_by = "mock" },
                new { id = "mock/other-model", @object = "model", created = 0, owned_by = "mock" }
            }
        };
        string json = JsonSerializer.Serialize(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = body.Length;
        await resp.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private async Task WriteChatCompletionAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
    {
        // Parse body to record the request + extract model.
        string requestBody;
        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
            requestBody = await sr.ReadToEndAsync(ct).ConfigureAwait(false);

        string? model = null;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                model = m.GetString();
        }
        catch
        { /* malformed; record raw body */
        }

        lock (_receivedLock)
        {
            _received.Add(new ChatCompletionRequest(model ?? "", requestBody));
        }

        CannedResponse canned;
        lock (_responsesLock)
        {
            canned = model is not null && _responses.TryGetValue(model, out var r)
                ? r
                : new CannedResponse("(no mock response configured for model '" + model + "')",
                    false, null, null);
        }

        resp.ContentType = "text/event-stream";
        resp.ContentEncoding = Encoding.UTF8;
        var outStream = resp.OutputStream;

        if (canned.IsToolCall)
        {
            // Single tool-call chunk + finish.
            string toolCallChunk = BuildToolCallChunk(model ?? "mock", canned.ToolName!, canned.ToolArgs!);
            await WriteSseAsync(outStream, toolCallChunk, ct).ConfigureAwait(false);
            string finishChunk = BuildFinishChunk(model ?? "mock", "tool_calls");
            await WriteSseAsync(outStream, finishChunk, ct).ConfigureAwait(false);
        }
        else
        {
            // Stream the canned text in 4-char chunks. Real providers stream
            // per-token; 4-char chunks are visually similar and fast enough
            // for tests (no Thread.Sleep between chunks).
            string text = canned.Text ?? string.Empty;
            for (int i = 0; i < text.Length; i += 4)
            {
                int len = Math.Min(4, text.Length - i);
                string slice = text.Substring(i, len);
                string chunk = BuildTextDeltaChunk(model ?? "mock", slice);
                await WriteSseAsync(outStream, chunk, ct).ConfigureAwait(false);
            }
            string finishChunk = BuildFinishChunk(model ?? "mock", "stop");
            await WriteSseAsync(outStream, finishChunk, ct).ConfigureAwait(false);
        }

        await WriteSseDoneAsync(outStream, ct).ConfigureAwait(false);
    }

    private static async Task WriteSseAsync(Stream s, string jsonPayload, CancellationToken ct)
    {
        byte[] line = Encoding.UTF8.GetBytes("data: " + jsonPayload + "\n\n");
        await s.WriteAsync(line, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSseDoneAsync(Stream s, CancellationToken ct)
    {
        byte[] line = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await s.WriteAsync(line, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string BuildTextDeltaChunk(string model, string deltaText)
    {
        var chunk = new
        {
            id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content = deltaText },
                    finish_reason = (string?)null
                }
            }
        };
        return JsonSerializer.Serialize(chunk);
    }

    private static string BuildFinishChunk(string model, string finishReason)
    {
        // Final chunk: empty delta + finish_reason + usage (the client looks
        // for prompt_tokens / completion_tokens to surface usage stats).
        var chunk = new
        {
            id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content = "" },
                    finish_reason = finishReason
                }
            },
            usage = new
            {
                prompt_tokens = 8,
                completion_tokens = 4,
                total_tokens = 12
            }
        };
        return JsonSerializer.Serialize(chunk);
    }

    private static string BuildToolCallChunk(string model, string toolName, string argsJson)
    {
        var chunk = new
        {
            id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = "call_mock_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                                type = "function",
                                function = new { name = toolName, arguments = argsJson }
                            }
                        }
                    },
                    finish_reason = (string?)null
                }
            }
        };
        return JsonSerializer.Serialize(chunk);
    }

    private readonly record struct CannedResponse(
        string? Text,
        bool IsToolCall,
        string? ToolName,
        string? ToolArgs);
}

/// <summary>
///     Recorded copy of a chat-completion request received by
///     <see cref="MockLlmServer" />. The raw JSON body is preserved verbatim so
///     tests can assert on any field without the mock having to model the
///     entire request schema.
/// </summary>
public sealed record ChatCompletionRequest(string Model, string RawBody);
