using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using TUnit.Assertions.Extensions;

namespace Harbor.Ipc.Tests;

/// <summary>
///     SPIKE for #21 (throwaway, not product code): minimal late-join / replay /
///     scrollback-resync over Unix-domain sockets + NDJSON (one JSON object per
///     line, <see cref="System.Text.Json"/> only — no MessagePack, no new packages).
///     <para/>
///     The production IPC path (<c>MessagePackRpcServer</c> + <c>EventBroadcaster</c>)
///     already implements replay over MessagePack framing (see
///     <c>ReconnectReplayTests</c>); this spike answers the narrower #21 question —
///     whether the same semantics carry over a plain NDJSON wire — without
///     productizing anything: every helper below is file-local, nothing is
///     registered in DI, no new project/axis/transport is introduced.
/// </summary>
[NotInParallel("ipc")]
public sealed class NdjsonUdsReplaySpikeTests
{
    private static string UniqueSocketPath()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(Path.GetTempPath(), $"hb21-{id}.sock");
    }

    [Test]
    public async Task LateJoin_ReplaysMissedRange_InOrder_ThenLive()
    {
        if (OperatingSystem.IsWindows())
            return; // spike targets UDS; Windows CI uses named pipes.

        await using var server = new SpikeServer(UniqueSocketPath(), capacity: 64);
        server.Start();

        for (int i = 1; i <= 5; i++)
            await server.PublishAsync($"turn-{i}");

        await using var client = new SpikeClient();
        await client.ConnectAsync(server.SocketPath);
        await client.SendSubscribeAsync(since: 2);

        var sw = Stopwatch.StartNew();
        var replayed = new List<(ulong Seq, string Data)>();
        for (int i = 0; i < 3; i++)
            replayed.Add(await client.ReadEventAsync(TimeSpan.FromSeconds(10)));
        sw.Stop();

        for (int i = 0; i < 3; i++)
        {
            await Assert.That(replayed[i].Seq).IsEqualTo((ulong)(3 + i));
            await Assert.That(replayed[i].Data).IsEqualTo($"turn-{3 + i}");
        }

        // Live tail: publish after subscribe still arrives on the same connection.
        await server.PublishAsync("turn-6");
        var live = await client.ReadEventAsync(TimeSpan.FromSeconds(10));
        await Assert.That(live.Seq).IsEqualTo((ulong)6);
        await Assert.That(live.Data).IsEqualTo("turn-6");

        Console.WriteLine($"SPIKE21 late-join: replayed 3/3 in order + 1 live in {sw.ElapsedMilliseconds}ms (UDS+NDJSON).");
    }

    [Test]
    public async Task StaleSince_YieldsResync_NoReplayStorm()
    {
        if (OperatingSystem.IsWindows())
            return; // spike targets UDS; Windows CI uses named pipes.

        await using var server = new SpikeServer(UniqueSocketPath(), capacity: 8);
        server.Start();

        for (int i = 1; i <= 20; i++)
            await server.PublishAsync($"turn-{i}");

        await using var client = new SpikeClient();
        await client.ConnectAsync(server.SocketPath);
        await client.SendSubscribeAsync(since: 3); // long evicted from the 8-ring.

        var resync = await client.ReadResyncAsync(TimeSpan.FromSeconds(10));
        await Assert.That(resync.Resync).IsTrue();
        await Assert.That(resync.ServerSeq).IsEqualTo((ulong)20);

        // No replay storm follows the resync signal within the window.
        bool anyEvent = await client.TryReadEventAsync(TimeSpan.FromMilliseconds(500));
        await Assert.That(anyEvent).IsFalse();

        Console.WriteLine("SPIKE21 scrollback-evicted: stale since=3 against serverSeq=20 -> resync, 0 replay frames.");
    }

    [Test]
    public async Task Replay_Throughput_ReportsNdjsonOverUds()
    {
        if (OperatingSystem.IsWindows())
            return; // spike targets UDS; Windows CI uses named pipes.

        const int count = 1000;
        await using var server = new SpikeServer(UniqueSocketPath(), capacity: 2000);
        server.Start();

        string payload = new string('x', 64);
        for (int i = 1; i <= count; i++)
            await server.PublishAsync($"{payload}-{i}");

        await using var client = new SpikeClient();
        await client.ConnectAsync(server.SocketPath);
        await client.SendSubscribeAsync(since: 0);

        var sw = Stopwatch.StartNew();
        ulong expected = 1;
        long bytes = 0;
        for (int i = 0; i < count; i++)
        {
            var (seq, data) = await client.ReadEventAsync(TimeSpan.FromSeconds(15));
            await Assert.That(seq).IsEqualTo(expected);
            bytes += data.Length;
            expected++;
        }
        sw.Stop();

        double secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine(
            $"SPIKE21 throughput: replayed {count} events (~{(bytes + count * 24) / 1024} KiB NDJSON) " +
            $"in {sw.ElapsedMilliseconds}ms = {(count / secs):F0} ev/s over UDS+NDJSON.");

        await Assert.That(sw.Elapsed < TimeSpan.FromSeconds(15)).IsTrue();
    }

    // ── Throwaway wire helpers (file-local; not product code) ───────────────

    /// <summary>
    ///     Minimal NDJSON event log: monotonically sequenced frames in a bounded
    ///     ring; late subscribers get the missed range or a resync signal.
    /// </summary>
    private sealed class SpikeServer : IAsyncDisposable
    {
        private readonly Lock _mu = new();
        private readonly Queue<(ulong Seq, string Data)> _ring = new();
        private readonly List<(StreamWriter Writer, SemaphoreSlim Gate)> _live = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly Socket _listener;
        private Task? _acceptLoop;
        private ulong _seq;
        private int _disposed;

        public SpikeServer(string socketPath, int capacity)
        {
            SocketPath = socketPath;
            Capacity = capacity;
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        }

        public string SocketPath { get; }

        public int Capacity { get; }

        public void Start()
        {
            try { File.Delete(SocketPath); }
            catch (IOException) { }
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(backlog: 8);
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        public async Task PublishAsync(string data)
        {
            List<(StreamWriter Writer, SemaphoreSlim Gate)> targets;
            (ulong Seq, string Data) frame;
            lock (_mu)
            {
                _seq++;
                frame = (_seq, data);
                _ring.Enqueue(frame);
                while (_ring.Count > Capacity)
                    _ring.Dequeue();
                targets = [.. _live];
            }

            string line = JsonSerializer.Serialize(new { seq = frame.Seq, data = frame.Data });
            foreach (var (writer, gate) in targets)
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException) { }
                finally
                {
                    gate.Release();
                }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Socket conn;
                try
                {
                    conn = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _ = HandleClientAsync(conn, ct);
            }
        }

        private async Task HandleClientAsync(Socket conn, CancellationToken ct)
        {
            await using var stream = new NetworkStream(conn, ownsSocket: true);
            using var reader = new StreamReader(stream, leaveOpen: true);
            var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
            var gate = new SemaphoreSlim(1, 1);

            string? subLine;
            try
            {
                subLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (subLine is null)
                return;

            ulong? since = null;
            try
            {
                using var doc = JsonDocument.Parse(subLine);
                if (doc.RootElement.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number)
                    since = s.GetUInt64();
            }
            catch (JsonException)
            {
                return;
            }

            List<(ulong Seq, string Data)> replay = [];
            bool resync = false;
            ulong serverSeq;
            lock (_mu)
            {
                serverSeq = _seq;
                if (since.HasValue)
                {
                    ulong oldest = _ring.Count == 0 ? _seq + 1 : _ring.Peek().Seq;
                    if (since.Value + 1 < oldest)
                    {
                        resync = true;
                    }
                    else
                    {
                        replay = _ring.Where(f => f.Seq > since.Value).ToList();
                    }
                }

                if (!resync)
                    _live.Add((writer, gate));
            }

            try
            {
                if (resync)
                {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(new { resync = true, serverSeq })).ConfigureAwait(false);
                    return;
                }

                foreach (var f in replay)
                {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(new { seq = f.Seq, data = f.Data })).ConfigureAwait(false);
                }

                // Hold the connection for live fanout until teardown.
                try
                {
                    while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is not null)
                    {
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
            finally
            {
                lock (_mu)
                {
                    _live.Remove((writer, gate));
                }
                gate.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener.Dispose(); }
            catch (SocketException) { }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            try { File.Delete(SocketPath); }
            catch (IOException) { }
        }
    }

    /// <summary>Minimal NDJSON client: one subscribe line, then framed reads.</summary>
    private sealed class SpikeClient : IAsyncDisposable
    {
        private Socket? _socket;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private int _disposed;

        public async Task ConnectAsync(string socketPath)
        {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
            _stream = new NetworkStream(_socket, ownsSocket: true);
            _reader = new StreamReader(_stream, leaveOpen: true);
            _writer = new StreamWriter(_stream, leaveOpen: true) { AutoFlush = true };
        }

        public Task SendSubscribeAsync(ulong? since)
        {
            return _writer!.WriteLineAsync(
                since.HasValue
                    ? JsonSerializer.Serialize(new { op = "subscribe", since = since.Value })
                    : JsonSerializer.Serialize(new { op = "subscribe" }));
        }

        public async Task<(ulong Seq, string Data)> ReadEventAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            string? line = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null)
                throw new IOException("SPIKE21: server closed the connection mid-replay.");
            using var doc = JsonDocument.Parse(line);
            return (doc.RootElement.GetProperty("seq").GetUInt64(), doc.RootElement.GetProperty("data").GetString()!);
        }

        public async Task<(bool Resync, ulong ServerSeq)> ReadResyncAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            string? line = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null)
                throw new IOException("SPIKE21: server closed the connection before resync.");
            using var doc = JsonDocument.Parse(line);
            return (doc.RootElement.GetProperty("resync").GetBoolean(), doc.RootElement.GetProperty("serverSeq").GetUInt64());
        }

        public async Task<bool> TryReadEventAsync(TimeSpan window)
        {
            using var cts = new CancellationTokenSource(window);
            try
            {
                string? line = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
                return line is not null;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            try
            {
                if (_writer is not null)
                    await _writer.DisposeAsync().ConfigureAwait(false);
                _reader?.Dispose();
                if (_stream is not null)
                    await _stream.DisposeAsync().ConfigureAwait(false);
                _socket?.Dispose();
            }
            catch (IOException) { }
        }
    }
}
