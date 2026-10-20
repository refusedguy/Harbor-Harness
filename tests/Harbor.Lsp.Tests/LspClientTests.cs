using System.Text.Json;

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
