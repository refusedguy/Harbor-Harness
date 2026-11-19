using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>Framing and demux behavior of the client half of the wire protocol.</summary>
public class LspClientTests
{
    [Test]
    [Timeout(30_000)]
    public async Task Request_Response_RoundTrips(CancellationToken cancellationToken)
    {
        await using var server = new FakeLspServer();
        server.OnRequest = (method, _) => method == "initialize"
            ? new Dictionary<string, object?> { ["capabilities"] = new Dictionary<string, object?>() }
            : null;
        server.Run();
        server.Client.Start();

        JsonElement? result = await server.Client.SendRequestAsync(
            "initialize",
            JsonSerializer.SerializeToElement(
                new LspInitializeParams(1, "file:///tmp"), LspJsonContext.Default.LspInitializeParams),
            cancellationToken);

        await Assert.That(result is not null).IsTrue();
        await Assert.That(result!.Value.TryGetProperty("capabilities", out _)).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task ParamDto_SerializesThroughContext_PreservesWireFormat(CancellationToken cancellationToken)
    {
        await using var server = new FakeLspServer();
        JsonElement? capturedParams = null;
        server.OnRequest = (_, parameters) =>
        {
            capturedParams = parameters;
            return new Dictionary<string, object?>();
        };
        server.Run();
        server.Client.Start();

        var parameters = new LspWire.PositionParams(
            new LspWire.TextDocumentIdentifier("file:///tmp/a.ts"),
            new LspWire.LspPosition(2, 7));

        _ = await server.Client.SendRequestAsync("textDocument/definition", parameters, cancellationToken);

        await Assert.That(capturedParams is not null).IsTrue();
        await Assert.That(capturedParams!.Value.GetRawText()).IsEqualTo(
            """{"textDocument":{"uri":"file:///tmp/a.ts"},"position":{"line":2,"character":7}}""");
    }

    [Test]
    [Timeout(30_000)]
    public async Task ErrorResponse_ThrowsLspRequestException(CancellationToken cancellationToken)
    {
        await using var server = new FakeLspServer();
        server.OnError = (_, _) => "boom";
        server.Run();
        server.Client.Start();

        Task requestTask = server.Client.SendRequestAsync(
            "textDocument/definition", null,
            cancellationToken);

        try
        {
            await requestTask;
            Assert.Fail("Should have thrown LspRequestException");
        }
        catch (LspRequestException ex)
        {
            await Assert.That(ex.Message).IsEqualTo("boom");
        }
    }

    [Test]
    public async Task ServerNotification_SurfacesThroughEvent()
    {
        await using var server = new FakeLspServer();
        server.Run();
        server.Client.Start();

        TaskCompletionSource<LspNotificationEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Client.ServerNotification += (_, args) => received.TrySetResult(args);
        await server.NotifyAsync("textDocument/publishDiagnostics",
            new Dictionary<string, object?> { ["uri"] = "file:///tmp/a.ts", ["diagnostics"] = Array.Empty<object>() });

        LspNotificationEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(args.Method).IsEqualTo("textDocument/publishDiagnostics");
        await Assert.That(args.Parameters.GetProperty("uri").GetString()).IsEqualTo("file:///tmp/a.ts");
    }

    [Test]
    public async Task StalledServer_RequestCancelled_DoesNotHang()
    {
        // The fake server never answers (Run() not called): per-request CTS must abort the wait.
        await using var server = new FakeLspServer();
        server.Client.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TaskCanceledException>(() => server.Client.SendRequestAsync(
            "initialize", null, cts.Token));
    }

    [Test]
    [Timeout(30_000)]
    public async Task ServerExit_EndsReadLoop_CancelsPendingWithoutDispose()
    {
        // #3061 item 2: an abrupt server exit is seen by the read loop, not by
        // Dispose. The pending request must be cancelled by EOF alone. Using
        // the test's await-using to dispose the client would hide the race by
        // cancelling the waiter on a completely different path.
        var fromServer = new MemPipeStream();
        var toServer = new MemPipeStream();
        await using var client = new LspClient(fromServer, toServer, NullLogger.Instance);

        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => disconnected.TrySetResult();
        client.Start();

        Task pending = client.SendRequestAsync("initialize", null, CancellationToken.None);
        await Task.Yield(); // let the unanswered frame reach the pipe

        fromServer.End(); // simulate the child closing its stdout

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TaskCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    [Timeout(30_000)]
    public async Task ServerExit_RaisesDisconnected_AndCancelsPending(CancellationToken cancellationToken)
    {
        await using var server = new FakeLspServer();
        server.Run();
        server.Client.Start();

        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Client.Disconnected += (_, _) => disconnected.TrySetResult();

        Task requestTask = server.Client.SendRequestAsync(
            "initialize", null, cancellationToken);
        await server.DisposeAsync(); // pull the pipes

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TaskCanceledException>(() => requestTask);
    }
}
