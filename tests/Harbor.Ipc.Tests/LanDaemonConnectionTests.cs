using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Harbor.Ipc.Client;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Issue #1435 (partial): the networked-daemon userspace path the LAN /
///     tailscale stories depend on: a daemon bound on all interfaces and a
///     client dialing it by a real, non-loopback LAN address (not 127.0.0.1)
///     with the pre-shared key. TcpPskTests only ever dials loopback, so the
///     non-loopback path had no coverage. This test is hermetic (own host's
///     LAN interface) and runs in CI; the cross-host manual runs in the issue
///     remain a separate, HARBOR_E2E-gated concern.
/// </summary>
[NotInParallel("ipc")]
public class LanDaemonConnectionTests
{
    private const string Key = "dGVzdC1wc2sta2V5LTEyMzQ1Njc4OTA=";

    /// <summary>First up, non-loopback IPv4 unicast address, the address a same-LAN peer would dial.</summary>
    private static IPAddress? FindLanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(info.Address))
                {
                    return info.Address;
                }
            }
        }

        return null;
    }

    [Test]
    [Timeout(30_000)]
    public async Task LanAddress_DaemonReachableWithPsk_RoundTrips()
    {
        IPAddress? lan = FindLanAddress();
        Skip.Unless(lan is not null,
            "No non-loopback IPv4 interface on this host; the LAN dial path cannot be exercised here.");

        // The target is genuinely off-loopback, which is the whole point of this test.
        await Assert.That(IPAddress.IsLoopback(lan!)).IsFalse();

        var sp = TestHost.Build();
        var lf = sp.GetRequiredService<ILoggerFactory>();

        // "0.0.0.0" is the all-interfaces listen policy (HARBOR_LISTEN=all):
        // the daemon advertises on the LAN, not just to local clients.
        var transport = new TcpServerTransport("0.0.0.0", 0, lf.CreateLogger<TcpServerTransport>());
        var server = new HarborIpcServer(sp, transport, lf, Key);
        await server.StartAsync();
        try
        {
            int port = transport.BoundPort!.Value;
            await using var client = new IpcHarborClient(lan!.ToString(), port,
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
