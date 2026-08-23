using System.Net.Sockets;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests.Fuzz;

/// <summary>
///     Skip unless explicitly opted in via <c>HARBOR_IPC_FUZZ=1</c>. Real-server tests
///     spin actual named pipes / unix sockets; the project is excluded from the solution
///     because parallel pipe tests deadlock under TUnit parallel scheduling on Linux
///     (see Harbor.slnx comment). This attribute keeps the fuzz coverage one env var
///     away without destabilizing default runs.
/// </summary>
internal sealed class SkipUnlessIpcFuzzEnabledAttribute : SkipAttribute
{
    public SkipUnlessIpcFuzzEnabledAttribute() : base(
        "Real-server fuzz disabled by default (named-pipe tests deadlock under TUnit "
        + "parallel scheduling on Linux; project excluded from solution). "
        + "Set HARBOR_IPC_FUZZ=1 to enable.") { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(Environment.GetEnvironmentVariable("HARBOR_IPC_FUZZ") != "1");
}

/// <summary>
///     Server-level fuzz: blast garbage frames (zero-length keep-alives and undecodable
///     payloads — the two classes the committed policy skips) at a real
///     <see cref="MessagePackRpcServer" /> connection, then prove the same connection is
///     still usable for a valid request/response round-trip and that a fresh client can
///     connect afterwards (server accept loop survived).
/// </summary>
public class RpcServerFuzzTests
{
    [Test]
    [SkipUnlessIpcFuzzEnabled]
    public async Task GarbageFrames_SkippedByServer_ConnectionAndServerStayAlive()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-fuzz");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 1. Raw adversarial connection.
        await using (Stream raw = await ConnectRawAsync(pipe, timeoutCts.Token))
        {
            byte[] garbagePayload = [0xC1, 0xDE, 0xAD, 0xBE, 0xEF]; // never-valid msgpack start
            for (int i = 0; i < 3; i++)
                await raw.WriteAsync(new byte[4], timeoutCts.Token);          // zero-length frame

            for (int i = 0; i < 3; i++)
            {
                await raw.WriteAsync(ResilientFrameReaderProbe.LengthHeader((uint)garbagePayload.Length), timeoutCts.Token);
                await raw.WriteAsync(garbagePayload, timeoutCts.Token);       // undecodable frame
            }

            // 2. Same connection must still complete a valid request/response round-trip.
            var request = new ListToolsRequest();
            await WireCodec.WriteRequestAsync(raw, request, timeoutCts.Token);
            var response = await WireCodec.ReadResponseAsync(raw, timeoutCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), timeoutCts.Token);

            var ok = response as OkResponse;
            await Assert.That(ok).IsNotNull();
            await Assert.That(ok!.RequestId).IsEqualTo(request.RequestId);

            // 3. Abrupt mid-frame disconnect must not poison the server.
            await raw.WriteAsync(ResilientFrameReaderProbe.LengthHeader(1000), timeoutCts.Token);
        }

        // 4. A fresh well-behaved client proves the server/accept loop stayed alive.
        await using var client = new IpcHarborClient(
            pipe, sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
        await client.ConnectAsync();

        var create = await client.CreateSessionAsync("/tmp/fuzz", "code", "ollama", "qwen2.5-coder:7b")
            .WaitAsync(TimeSpan.FromSeconds(15), timeoutCts.Token);
        await Assert.That(create.IsSuccess).IsTrue();

        await server.StopAsync();
    }

    /// <summary>Open a raw transport stream to the server endpoint (pipe name or .sock path).</summary>
    private static async Task<Stream> ConnectRawAsync(string pipeName, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, ct);
            return pipe;
        }

        string socketPath = Path.Combine(Path.GetTempPath(), pipeName + ".sock");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
