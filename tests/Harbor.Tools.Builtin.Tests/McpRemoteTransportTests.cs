using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Remote MCP transports (streamable HTTP + legacy SSE) and their registry
///     integration, verified against local fake MCP servers.
/// </summary>
public class McpRemoteTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // ---------- Streamable HTTP ----------

    [Test]
    public async Task HttpTransport_JsonResponse_ReturnsMatchingFrame()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static body => $$"""{"jsonrpc":"2.0","id":{{JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32()}},"result":{"tools":[]} }""";

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":42,"method":"tools/list","params":{}}""");
        using JsonDocument? response = await transport.RoundTripAsync(request.RootElement.Clone(), 42);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RootElement.GetProperty("id").GetInt32()).IsEqualTo(42);
        await Assert.That(server.RequestBodies).Contains(b => b.Contains("tools/list"));
    }

    [Test]
    public async Task HttpTransport_Transient500_IsRetried()
    {
        using FakeServer server = FakeServer.Start();
        server.QueueStatusCodes.AddRange([500, 200]);
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        using JsonDocument? response = await transport.RoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(response).IsNotNull();
        await Assert.That(server.HandledRequests).IsEqualTo(2);    }

    [Test]
    public async Task HttpTransport_OAuthToken_IsAttachedAsBearer()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url, oauthTokenProvider: _ => Task.FromResult<string?>("tok-123"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        await transport.RoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(server.AuthHeaders).Contains("Bearer tok-123");
    }

    [Test]
    public async Task HttpTransport_SessionIdFromFirstResponse_IsReplayed()
    {
        using FakeServer server = FakeServer.Start();
        server.SessionIdToAssign = "sess-7";
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        await transport.RoundTripAsync(request.RootElement.Clone(), 1);
        await transport.RoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(server.SessionIds.Skip(1)).Contains("sess-7");
    }

    [Test]
    public async Task HttpTransport_SseResponseStream_IsParsed()
    {
        using FakeServer server = FakeServer.Start();
        server.SseResponseBody =
            """
            event: message
            data: {"jsonrpc":"2.0","id":7,"result":{"ok":true}}

            """;

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""");
        using JsonDocument? response = await transport.RoundTripAsync(request.RootElement.Clone(), 7);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RootElement.GetProperty("result").GetProperty("ok").GetBoolean()).IsTrue();
    }

    // ---------- Legacy HTTP + SSE ----------

    [Test]
    public async Task SseTransport_EndpointAnnounce_ThenResponseFrame()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":3,"result":{"echo":true}}""";

        await using var transport = new McpSseTransport(new Uri(server.Url + "/sse"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}""");
        using JsonDocument? response = await transport.RoundTripAsync(request.RootElement.Clone(), 3);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RootElement.GetProperty("result").GetProperty("echo").GetBoolean()).IsTrue();
        await Assert.That(server.RequestBodies.Any(b => b.Contains("tools/list"))).IsTrue();
    }

    // ---------- Registry integration ----------

    [Test]
    public async Task Registry_RemoteUrlConfig_InvokesOverHttp()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static body =>
            $$"""{"jsonrpc":"2.0","id":{{JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32()}},"result":{"tools":[{"name":"echo"}] } }""";

        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, $$"""
                {
                    "mcpServers": {
                        "remote": { "url": "{{server.Url}}", "transport": "http" }
                    }
                }
                """);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>());
            await Assert.That(registry.RegisterFromConfig(tempFile).IsSuccess).IsTrue();

            using var args = JsonDocument.Parse("{}");
            Result<string> result = await registry.InvokeAsync("remote", "tools/list", args.RootElement);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).Contains("echo");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Registry_RegisterUrl_RejectsInvalidTransportAndUrl()
    {
        var registry = new McpRegistry(null);
        await Assert.That(registry.Register("a", "not-a-url", "http").IsSuccess).IsFalse();
        await Assert.That(registry.Register("b", "http://localhost/x", "grpc").IsSuccess).IsFalse();
        await Assert.That(registry.Register("c", "http://localhost/x", "sse").IsSuccess).IsTrue();
    }

    // ---------- Fake MCP server ----------

    /// <summary>Minimal HTTP fake: streamable-JSON, SSE-bodied and legacy-SSE MCP servers in one.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));
        private readonly Task _acceptLoop;

        public Uri Url { get; }
        public List<int> QueueStatusCodes { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<string?> AuthHeaders { get; } = [];
        public List<string?> SessionIds { get; } = [];
        public int HandledRequests { get; private set; }
        public string? SessionIdToAssign { get; set; }
        public string? SseResponseBody { get; set; }
        public Func<string, string>? JsonResponder { get; set; }

        private readonly TaskCompletionSource _postArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeServer(HttpListener listener, Uri url)
        {
            _listener = listener;
            Url = url;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static FakeServer Start()
        {
            int port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new FakeServer(listener, new Uri($"http://127.0.0.1:{port}/mcp"));
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                HandledRequests++;
                AuthHeaders.Add(context.Request.Headers["Authorization"]);
                SessionIds.Add(context.Request.Headers["Mcp-Session-Id"]);

                bool isGet = context.Request.HttpMethod == "GET";
                using var reader = new StreamReader(context.Request.InputStream);
                string body = isGet ? string.Empty : await reader.ReadToEndAsync(_cts.Token);

                if (isGet)
                {
                    // Legacy SSE channel: announce the POST endpoint, then emit the response.
                    context.Response.ContentType = "text/event-stream";
                    await WriteSseAsync(context.Response, $"event: endpoint\ndata: /message\n\n");
                    // The client POSTs next; once handled, emit the response frame.
                    await WaitForPostAsync(context.Response);
                }
                else
                {
                    RequestBodies.Add(body);
                    _postArrived.TrySetResult();

                    if (SessionIdToAssign is { } sessionId)
                    {
                        context.Response.Headers["Mcp-Session-Id"] = sessionId;
                    }

                    if (SseResponseBody is not null)
                    {
                        context.Response.ContentType = "text/event-stream";
                        byte[] sse = Encoding.UTF8.GetBytes(SseResponseBody);
                        await context.Response.OutputStream.WriteAsync(sse, _cts.Token);
                    }
                    else
                    {
                        int status = QueueStatusCodes.Count > 0 ? QueueStatusCodes[0] : 200;
                        if (QueueStatusCodes.Count > 0)
                        {
                            QueueStatusCodes.RemoveAt(0);
                        }

                        context.Response.StatusCode = status;
                        if (status == 200 && JsonResponder is { } responder)
                        {
                            context.Response.ContentType = "application/json";
                            byte[] json = Encoding.UTF8.GetBytes(responder(body));
                            await context.Response.OutputStream.WriteAsync(json, _cts.Token);
                        }
                    }
                }

                context.Response.Close();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // shutdown
            }
            catch
            {
                try { context.Response.Close(); } catch { /* already gone */ }
            }
        }

        private async Task WriteSseAsync(HttpListenerResponse response, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await response.OutputStream.WriteAsync(bytes, _cts.Token);
            await response.OutputStream.FlushAsync(_cts.Token);
        }

        private async Task WaitForPostAsync(HttpListenerResponse response)
        {
            await _postArrived.Task.WaitAsync(_cts.Token);
            if (JsonResponder is { } responder)
            {
                string payload = $"event: message\ndata: {responder("{}")}\n\n";
                await WriteSseAsync(response, payload);
            }
        }

        private static int GetFreePort()
        {
            var probe = TcpListener.Create(0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* not started */ }
            try { _acceptLoop.Wait(1000); } catch { /* loop ended */ }
            _cts.Dispose();
        }
    }
}
