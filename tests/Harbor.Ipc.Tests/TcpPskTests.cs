using Harbor.Ipc.Protocol;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
namespace Harbor.Ipc.Tests;

/// <summary>
///     TCP transport + PSK gate tests (sprint 6 zone T / A2): a networked
///     daemon answers only after the pre-shared key handshake; wrong key
///     closes the connection; unauthenticated requests get the structured
///     PSK_REQUIRED error; and a second listener cannot steal the port.
/// </summary>
[NotInParallel]
public class TcpPskTests
{
    private const string Key = "dGVzdC1wc2sta2V5LTEyMzQ1Njc4OTA=";

    private static int FreePort()
    {
        var probe = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Dispose();
        return port;
    }

    private static (HarborIpcServer Server, IServiceProvider Sp, int Port) StartTcpServer(string? psk)
    {
        var sp = TestHost.Build();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var transport = new TcpServerTransport("127.0.0.1", 0, loggerFactory.CreateLogger<TcpServerTransport>());
        var server = new HarborIpcServer(sp, transport, loggerFactory, psk);
        server.StartAsync().GetAwaiter().GetResult();
        return (server, sp, transport.BoundPort!.Value);
    }

    private static async Task<(MessagePackRpcClient Client, IIpcClientTransport Transport)> DialRawAsync(
        string host, int port, string? psk, ILoggerFactory loggerFactory)
    {
        var transport = new TcpClientTransport(host, port, loggerFactory.CreateLogger<TcpClientTransport>());
        var client = new MessagePackRpcClient(transport, loggerFactory.CreateLogger<MessagePackRpcClient>(), psk);
        await client.ConnectAsync();
        return (client, transport);
    }

    [Test]
    public async Task Tcp_WithValidPsk_RoundTrips()
    {
        (var server, var sp, int port) = StartTcpServer(Key);
        try
        {
            await using var client = new IpcHarborClient("127.0.0.1", port,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>(), Key);
            await client.ConnectAsync();

            var result = await client.ListProvidersAsync();
            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Tcp_RequestWithoutPsk_GetsStructuredError()
    {
        (var server, var sp, int port) = StartTcpServer(psk: Key);
        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var transport = new TcpClientTransport("127.0.0.1", port, lf.CreateLogger<TcpClientTransport>());
            var client = new MessagePackRpcClient(transport, lf.CreateLogger<MessagePackRpcClient>(), null);
            await client.ConnectAsync();

            // Any request before authentication must be rejected with the
            // structured error — fail-closed, no service without the key.
            var response = await client.SendAsync(new ConnectRequest());
            await Assert.That(response).IsTypeOf<ErrorResponse>();
            await Assert.That(((ErrorResponse)response).Message).Contains("PSK_REQUIRED");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Tcp_WrongPsk_FailsFast_AndServerSurvives()
    {
        (var server, var sp, int port) = StartTcpServer(psk: Key);
        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();

            // The client-side handshake surfaces the server's structured
            // rejection as an IOException immediately on ConnectAsync.
            Exception? failure = null;
            try
            {
                await DialRawAsync("127.0.0.1", port, "wrong-key-entirely", lf);
            }
            catch (IOException ex)
            {
                failure = ex;
            }

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Message).Contains("PSK_AUTH_FAILED");

            // The daemon is unharmed: a correctly-keyed client connects.
            var (good, _) = await DialRawAsync("127.0.0.1", port, Key, lf);
            var ok = await good.SendAsync(new ConnectRequest());
            await Assert.That(ok).IsTypeOf<OkResponse>();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Tcp_SecondListenerOnSamePort_CannotSteal()
    {
        (var server, _, int port) = StartTcpServer(psk: null);
        try
        {
            var logger = TestLogger();
            var thief = new TcpServerTransport("127.0.0.1", port, logger);

            Exception? failure = null;
            try
            {
                await thief.BindAsync();
                await thief.UnbindAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Message).Contains("bind");
        }
        finally
        {
            await server.StopAsync();
        }

        static ILogger<TcpServerTransport> TestLogger() =>
            NullLogger<TcpServerTransport>.Instance;
    }

    [Test]
    public async Task Uds_WithPskGate_RequiresKey()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-psk-uds");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>(), Key);
        await server.StartAsync();
        try
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();

            // Without the key → structured refusal.
            var anonymous = new MessagePackRpcClient(
                new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>()),
                lf.CreateLogger<MessagePackRpcClient>());
            await anonymous.ConnectAsync();
            var denied = await anonymous.SendAsync(new ConnectRequest());
            await Assert.That(denied).IsTypeOf<ErrorResponse>();
            await Assert.That(((ErrorResponse)denied).Message).Contains("PSK_REQUIRED");

            // With the key → full service.
            var gated = new ClientPipeTransport(pipe, lf.CreateLogger<ClientPipeTransport>());
            await using var client = new IpcHarborClient(gated,
                lf.CreateLogger<IpcHarborClient>(), Key);
            await client.ConnectAsync();
            var result = await client.ListProvidersAsync();
            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
