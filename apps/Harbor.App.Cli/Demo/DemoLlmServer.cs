using System.Net;
using System.Text;
using System.Text.Json;

namespace Harbor.App.Cli.Demo;

/// <summary>One scripted LLM reply: streamed text or a single tool call.</summary>
public sealed record DemoReply(string? Text, string? ToolName, string? ToolArgsJson)
{
    public static DemoReply FromText(string text) => new(text, null, null);

    public static DemoReply FromToolCall(string toolName, string argsJson) => new(null, toolName, argsJson);
}

/// <summary>
///     In-process OpenAI-compatible mock LLM server backing <c>harbor demo</c>.
///     Serves a FIFO script of canned SSE chat completions so the demo plays
///     deterministically with zero API keys and zero network access.
/// </summary>
/// <remarks>
///     Wire shape mirrors the real OpenAI Chat Completions streaming protocol
///     (one <c>data: {chunk}</c> line per delta, <c>data: [DONE]</c> terminator),
///     plus a static <c>GET /models</c> list. Chunk payloads are assembled as
///     literal strings with <see cref="JsonEncodedText" /> for the variable parts —
///     no reflection-based serialization anywhere (AOT-safe, §PERF-002).
/// </remarks>
public sealed class DemoLlmServer : IAsyncDisposable
{
    /// <summary>Model id the demo provider config references (<c>demo/harbor-1</c>).</summary>
    public const string ModelId = "harbor-1";

    private readonly Queue<DemoReply> _script = new();
    private readonly object _scriptLock = new();
    private readonly TimeSpan _chunkDelay;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private Task? _loopTask;

    /// <summary>Create the server. <paramref name="chunkDelayMs" /> paces text deltas so streaming is visible in recordings.</summary>
    public DemoLlmServer(int chunkDelayMs = 30)
    {
        _chunkDelay = TimeSpan.FromMilliseconds(Math.Max(0, chunkDelayMs));
    }

    /// <summary>Base URI for the demo provider config (<c>http://localhost:&lt;port&gt;</c>, no trailing slash).</summary>
    public Uri BaseUri { get; private set; } = new("http://localhost:0");

    /// <summary>Append replies to the FIFO script. Each <c>chat/completions</c> request consumes the next entry.</summary>
    public void Enqueue(params ReadOnlySpan<DemoReply> replies)
    {
        lock (_scriptLock)
        {
            foreach (DemoReply reply in replies)
            {
                _script.Enqueue(reply);
            }
        }
    }

    /// <summary>Bind a random localhost port and start serving. Idempotent.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loopTask is not null)
        {
            return Task.CompletedTask;
        }

        _listener = new HttpListener();
        Exception? lastError = null;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            int port = Random.Shared.Next(50_000, 60_000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                BaseUri = new Uri($"http://localhost:{port}");
                _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
                return Task.CompletedTask;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                try { _listener.Stop(); }
                catch { /* port taken — retry on a fresh listener */ }

                _listener = new HttpListener();
            }
        }

        throw new InvalidOperationException("Could not bind DemoLlmServer to any port.", lastError);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); }
        catch { /* already stopped */ }
        try { _listener?.Close(); }
        catch { /* already closed */ }
        _cts.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        HttpListenerResponse resp = ctx.Response;
        try
        {
            string path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (ctx.Request.HttpMethod == "GET" && path is "/models" or "/v1/models")
            {
                await WriteAsync(resp, "application/json",
                    $$"""{"object":"list","data":[{"id":"{{ModelId}}","object":"model","created":0,"owned_by":"harbor-demo"}]}""", ct)
                    .ConfigureAwait(false);
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path is "/chat/completions" or "/v1/chat/completions")
            {
                await ServeCompletionAsync(resp, ct).ConfigureAwait(false);
                return;
            }

            resp.StatusCode = 404;
            await WriteAsync(resp, "text/plain", "not found", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try
            {
                resp.StatusCode = 500;
                await WriteAsync(resp, "text/plain", "demo mock error: " + ex.Message, ct).ConfigureAwait(false);
            }
            catch { /* connection gone — nothing to report to */ }
        }
        finally
        {
            try { resp.Close(); }
            catch { /* client vanished */ }
        }
    }

    private async Task ServeCompletionAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        DemoReply? reply;
        lock (_scriptLock)
        {
            reply = _script.Count > 0 ? _script.Dequeue() : null;
        }

        resp.ContentType = "text/event-stream";
        resp.ContentEncoding = Encoding.UTF8;
        Stream outStream = resp.OutputStream;

        if (reply is null)
        {
            await WriteSseAsync(outStream, TextChunk("(demo script exhausted — enqueue more replies)"), ct).ConfigureAwait(false);
            await WriteSseAsync(outStream, FinishChunk("stop"), ct).ConfigureAwait(false);
            await WriteSseDoneAsync(outStream, ct).ConfigureAwait(false);
            return;
        }

        if (reply.ToolName is not null)
        {
            await WriteSseAsync(outStream, ToolCallChunk(reply.ToolName, reply.ToolArgsJson ?? "{}"), ct).ConfigureAwait(false);
            await WriteSseAsync(outStream, FinishChunk("tool_calls"), ct).ConfigureAwait(false);
        }
        else
        {
            string text = reply.Text ?? string.Empty;
            // 4-char deltas paced by _chunkDelay so the TUI paints token-by-token.
            for (int i = 0; i < text.Length; i += 4)
            {
                int len = Math.Min(4, text.Length - i);
                await WriteSseAsync(outStream, TextChunk(text.Substring(i, len)), ct).ConfigureAwait(false);
                if (_chunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_chunkDelay, ct).ConfigureAwait(false);
                }
            }

            await WriteSseAsync(outStream, FinishChunk("stop"), ct).ConfigureAwait(false);
        }

        await WriteSseDoneAsync(outStream, ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerResponse resp, string contentType, string body, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        resp.ContentType = contentType;
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteSseAsync(Stream s, string payload, CancellationToken ct)
    {
        byte[] line = Encoding.UTF8.GetBytes("data: " + payload + "\n\n");
        await s.WriteAsync(line, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSseDoneAsync(Stream s, CancellationToken ct)
    {
        byte[] line = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await s.WriteAsync(line, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string TextChunk(string delta)
    {
        string content = JsonEncodedText.Encode(delta).Value;
        return string.Concat(
            "{\"id\":\"chatcmpl-demo\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"", ModelId,
            "\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"", content,
            "\"},\"finish_reason\":null}]}");
    }

    private static string FinishChunk(string finishReason)
    {
        return string.Concat(
            "{\"id\":\"chatcmpl-demo\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"", ModelId,
            "\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"", finishReason,
            "\"}],\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":4,\"total_tokens\":12}}");
    }

    private static string ToolCallChunk(string toolName, string argsJson)
    {
        string args = JsonEncodedText.Encode(argsJson).Value;
        return string.Concat(
            "{\"id\":\"chatcmpl-demo\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"", ModelId,
            "\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_demo_1\",\"type\":\"function\",",
            "\"function\":{\"name\":\"", JsonEncodedText.Encode(toolName).Value,
            "\",\"arguments\":\"", args,
            "\"}}]},\"finish_reason\":null}]}");
    }
}
