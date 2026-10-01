using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Resilience;
using Harbor.Tools.Mcp;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>TEMPORARY diagnostic v2 for #925 — tallies instead of long strings so TUnit does not truncate.</summary>
public class ZzDiag925
{
    [Test]
    public async Task Zz925_D_RawSeamTally()
    {
        using var diagServer = RstServer.Start();
        using var client = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        SortedDictionary<string, int> tally = [];
        for (int i = 0; i < 40; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{diagServer.Port}/sse");
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            try
            {
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                Bump(tally, $"NO-THROW/{(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                Bump(tally, $"{ex.GetType().Name}/retry={TransientFailurePolicy.ShouldRetry(ex)}");
            }
        }

        await Assert.That(Fmt(tally)).IsEqualTo("DIAG-D");
    }

    [Test]
    public async Task Zz925_E_SseAttemptTally()
    {
        SortedDictionary<string, int> attempts = [];
        SortedDictionary<string, int> causes = [];
        for (int i = 0; i < 10; i++)
        {
            using var server = RstServer.Start();
            await using var transport = new McpSseTransport(
                new Uri($"http://127.0.0.1:{server.Port}/sse"),
                requestTimeout: TimeSpan.FromSeconds(5));
            using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":8,"method":"tools/list","params":{}}""");

            Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 8);

            string error = roundTrip.IsFailure ? roundTrip.Error : "SUCCESS";
            Match m = Regex.Match(error, @"after (\d+) attempt\(s\) in (\d+)ms: (.*)$");
            Bump(attempts, m.Success ? $"attempts={m.Groups[1].Value}/ms={m.Groups[2].Value}" : "UNPARSED");
            Bump(causes, m.Success ? m.Groups[3].Value : error);
            Bump(attempts, $"accepts={server.Accepted}");
        }

        await Assert.That(Fmt(attempts) + " || " + Fmt(causes)).IsEqualTo("DIAG-E");
    }

    [Test]
    public async Task Zz925_F_HttpAttemptTally()
    {
        SortedDictionary<string, int> attempts = [];
        SortedDictionary<string, int> causes = [];
        for (int i = 0; i < 10; i++)
        {
            using var server = RstServer.Start();
            await using var transport = new McpHttpTransport(
                new Uri($"http://127.0.0.1:{server.Port}/mcp"),
                requestTimeout: TimeSpan.FromSeconds(5));
            using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":9,"method":"tools/list","params":{}}""");

            Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 9);

            string error = roundTrip.IsFailure ? roundTrip.Error : "SUCCESS";
            Match m = Regex.Match(error, @"after (\d+) attempt\(s\) in (\d+)ms: (.*)$");
            Bump(attempts, m.Success ? $"attempts={m.Groups[1].Value}/ms={m.Groups[2].Value}" : "UNPARSED");
            Bump(causes, m.Success ? m.Groups[3].Value : error);
            Bump(attempts, $"accepts={server.Accepted}");
        }

        await Assert.That(Fmt(attempts) + " || " + Fmt(causes)).IsEqualTo("DIAG-F");
    }

    private static void Bump(SortedDictionary<string, int> tally, string key)
    {
        string k = key.Length > 60 ? key[..60] : key;
        tally[k] = tally.TryGetValue(k, out int n) ? n + 1 : 1;
    }

    private static string Fmt(SortedDictionary<string, int> tally)
        => string.Join(" ", tally.Select(kv => $"{kv.Key}x{kv.Value}"));

    /// <summary>Accepts TCP and hangs up with RST, counting accepts — the #925 DeadServer shape.</summary>
    private sealed class RstServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(120));
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