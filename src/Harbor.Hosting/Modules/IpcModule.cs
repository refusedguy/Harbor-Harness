using Harbor.Ipc;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class IpcModule
{
    /// <summary>HARBOR_MODE dispatcher: inprocess / ipc-server / ipc-client.</summary>
    internal static IServiceCollection AddHarborIpc(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        ctx.Logger.LogInformation("HARBOR_MODE = {Mode}", mode);

        string pipeName = Environment.GetEnvironmentVariable("HARBOR_IPC_PIPE") ?? "harbor-ipc";

        switch (mode.ToLowerInvariant())
        {
            case "inprocess":
                services.UseInProcessHarborClient();
                break;
            case "ipc-server":
                services.UseInProcessHarborClient();
                services.UseHarborIpcServer(pipeName);
                AddNetworkedListenerIfConfigured(services, ctx);
                break;
            case "ipc-client":
                services.UseIpcHarborClient(pipeName);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown HARBOR_MODE: '{mode}'. Expected one of: inprocess, ipc-server, ipc-client.");
        }
        return services;
    }

    /// <summary>
    ///     Optional networked daemon listener (sprint 6 zone T): set
    ///     <c>HARBOR_LISTEN</c> to loopback | tailscale0 | all (port via
    ///     <c>HARBOR_PORT</c>, default 48710). The listener is always
    ///     PSK-gated with the key from ~/.harbor/daemon.psk (bootstrapped on
    ///     first run), and a <see cref="DaemonPairingInfo"/> is registered so
    ///     the CLI can print the pairing block.
    /// </summary>
    private static void AddNetworkedListenerIfConfigured(IServiceCollection services, HarborCompositionContext ctx)
    {
        string? listenOn = Environment.GetEnvironmentVariable("HARBOR_LISTEN");
        if (string.IsNullOrWhiteSpace(listenOn) || listenOn.Equals("uds", StringComparison.OrdinalIgnoreCase))
        {
            return; // local-only daemon (default)
        }

        var bindAddress = DaemonBindPolicy.ResolveBindAddress(listenOn);
        if (bindAddress.IsFailure) // §4.6-ok: fail-fast composition-root с РАЗНЫМИ типами исключений — Bind склеил бы диагностику.
        {
            throw new ArgumentException(bindAddress.Error);
        }

        int port = DaemonBindPolicy.DefaultPort;
        if (int.TryParse(Environment.GetEnvironmentVariable("HARBOR_PORT"), out int configured) &&
            configured is > 0 and <= 65535)
        {
            port = configured;
        }

        var psk = PskStore.LoadOrBootstrap(PskStore.DefaultPath);
        if (psk.IsFailure) // §4.6-ok: см. выше — типизированный fail-fast запуска демона.
        {
            throw new InvalidOperationException($"Networked listener requires a PSK: {psk.Error}");
        }

        string bindText = bindAddress.Value.ToString();
        ctx.Logger.LogInformation(
            "Networked IPC listener: {ListenOn} → {Address}:{Port} (PSK-gated)", listenOn, bindText, port);

        services.AddSingleton<IHarborServer>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var transport = new TcpServerTransport(bindText, port,
                loggerFactory.CreateLogger<TcpServerTransport>());
            return new HarborIpcServer(sp, transport, loggerFactory, psk.Value);
        });

        // Advertise address follows tailscale > lan > loopback priority so the
        // pairing QR carries an address peers can actually reach from outside
        // the LAN (tailscale0), never just eth0.
        string advertiseHost = DaemonBindPolicy.SelectAdvertiseAddress()?.ToString() ?? bindText;
        services.AddSingleton(new DaemonPairingInfo(advertiseHost, port, psk.Value));
    }
}
