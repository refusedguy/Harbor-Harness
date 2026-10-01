using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Resilience;
using Harbor.Tools.Mcp;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>TEMPORARY diagnostic for #925 — deleted before the PR is final.</summary>
public class ZzDiag925
{
    [Test]
    public async Task Zz925_A_RawSendAsyncSeamExceptionShape()
    {
        using var diagServer = RstServer.Start();
        using var client = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        List<string> shapes = [];
        for (int i = 0; i < 4; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{diagServer.Port}/sse");
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            try
            {
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                shapes.Add($"{i}:NO-THROW status={(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                shapes.Add(
                    $"{i}:{ex.GetType().Name}|inner={ex.InnerException?.GetType().Name ?? "-"}"
                    + $"|retry={TransientFailurePolicy.ShouldRetry(ex)}|msg={ex.Message}");
            }
        }

        await Assert.That(string.Join(" ;; ", shapes) + $" || accepted={diagServer.Accepted}")
            .IsEqualTo("DIAG925-A");
    }

    [Test]
    public async Task Zz925_B_SseRoundTripAttemptCount()
    {
        using var server = RstServer.Start();
        await using var transport = new McpSseTransport(
            new Uri($"http://127.0.0.1:{server.Port}/sse"),
            requestTimeout: TimeSpan.FromSeconds(5));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":8,"method":"tools/list","params":{}}""");

        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 8);

        int immediately = server.Accepted;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (server.Accepted < 2 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        await Assert.That(
                $"immediate={immediately} afterWait={server.Accepted} waitedMs={sw.ElapsedMilliseconds} "
                + $"success={roundTrip.IsSuccess} error={(roundTrip.IsFailure ? roundTrip.Error : "-")}")
            .IsEqualTo("DIAG925-B");
    }

    [Test]
    public async Task Zz925_C_HttpRoundTripAttemptCount()
    {
        using var server = RstServer.Start();
        await using var transport = new McpHttpTransport(
            new Uri($"http://127.0.0.1:{server.Port}/mcp"),
            requestTimeout: TimeSpan.FromSeconds(5));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":9,"method":"tools/list","params":{}}""");

        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 9);

        int immediately = server.Accepted;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (server.Accepted < 2 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        await Assert.That(
                $"immediate={immediately} afterWait={server.Accepted} waitedMs={sw.ElapsedMilliseconds} "
                + $"success={roundTrip.IsSuccess} error={(roundTrip.IsFailure ? roundTrip.Error : "-")}")
            .IsEqualTo("DIAG925-C");
    }

    /// <summary>Accepts TCP and hangs up with RST, counting accepts — the #925 DeadServer shape.</summary>
    private sealed class RstServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
        private readonly Task _loop;
        private int _accepted;

        private RstServer(TcpListener listener)
        {
            _listener = listener;
            _loop = Task.Run(AcceptAndDropAsync);
        }

        public int Port { get; private init; }

        public int Accepted => Volatile.Read(ref _accepted);

        public static RstServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new RstServer(listener) { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        }

        private async Task AcceptAndDropAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _accepted);
                try
                {
                    socket.LingerState = new LingerOption(true, 0);
                }
                catch (SocketException)
                {
                }

                socket.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}