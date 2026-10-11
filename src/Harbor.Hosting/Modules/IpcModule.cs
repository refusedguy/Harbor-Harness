using System.Collections.Frozen;
using CSharpFunctionalExtensions;
using Harbor.Ipc;
#if HARBOR_WITH_DAEMON
// Slice A (#1144): Client/Server/Transport assemblies leave the graph when the
// daemon is off (same convention as HARBOR_WITH_PLUGINS below).
using Harbor.Ipc.Client;
#endif
using Harbor.Ipc.InProcess;
using Harbor.Ipc.Protocol;
#if HARBOR_WITH_DAEMON
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class IpcModule
{
    /// <summary>
    ///     HARBOR_MODE dispatcher (issue #175: strategy registry —
    ///     <see cref="HarborModeRegistry"/> — instead of an inline switch):
    ///     inprocess / ipc-server / ipc-client. Unknown modes fail fast.
    /// </summary>
    internal static IServiceCollection AddHarborIpc(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        ctx.Logger.LogInformation("HARBOR_MODE = {Mode}", mode);

        string pipeName = Environment.GetEnvironmentVariable("HARBOR_IPC_PIPE") ?? "harbor-ipc";

        FrozenDictionary<string, IHarborModeStrategy> registry = HarborModeRegistry.Build();
        Maybe<IHarborModeStrategy> strategy = HarborModeRegistry.Resolve(registry, mode);
        if (strategy.HasNoValue)
        {
            // #581: derived from the registry the resolution actually used — this used to be
            // a hand-written `HarborModeRegistry.KnownIds` string that no test compared to
            // the strategy array, so a new mode could ship while the error text denied it.
            throw new ArgumentException(
                $"Unknown HARBOR_MODE: '{mode}'. Expected one of: {string.Join(", ", registry.Keys.Order(StringComparer.Ordinal))}.");
        }

        strategy.Value.Apply(new HarborModeContext(services, ctx, pipeName));
        return services;
    }

#if HARBOR_WITH_DAEMON
    /// <summary>
    ///     Optional networked daemon listener (sprint 6 zone T): set
    ///     <c>HARBOR_LISTEN</c> to loopback | tailscale0 | all (port via
    ///     <c>HARBOR_PORT</c>, default 48710). The listener is always
    ///     PSK-gated with the key from ~/.harbor/daemon.psk (bootstrapped on
    ///     first run), and a <see cref="DaemonPairingInfo"/> is registered so
    ///     the CLI can print the pairing block.
    /// </summary>
    /// <remarks>
    ///     Slice A (#1144): compiled only with the daemon on. The body names
    ///     Server-assembly types (<c>HarborIpcServer</c>,
    ///     <c>TcpServerTransport</c>); the only caller is the likewise-gated
    ///     <c>IpcServerHarborModeStrategy</c>.
    /// </remarks>
    internal static void AddNetworkedListenerIfConfigured(IServiceCollection services, HarborCompositionContext ctx)
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
            // #63: composition-root factory lambda — resolving here is
            // idiomatic MS DI, not service location.
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var transport = new TcpServerTransport(bindText, port,
                loggerFactory.CreateLogger<TcpServerTransport>());
            #pragma warning disable CFE0001
            // CFE0001 false positive. Baseline: docs/ROP-API-INVENTORY.md 5.
            // psk is guarded by a fail-fast: if (psk.IsFailure) throw ... at the top of this method,
            // so reaching line 86 implies success. A throwing guard, not an early return.
            return new HarborIpcServer(sp, transport, loggerFactory, psk.Value);
            #pragma warning restore CFE0001
        });

        // Advertise address follows tailscale > lan > loopback priority so the
        // pairing QR carries an address peers can actually reach from outside
        // the LAN (tailscale0), never just eth0.
        string advertiseHost = DaemonBindPolicy.SelectAdvertiseAddress()?.ToString() ?? bindText;
        services.AddSingleton(new DaemonPairingInfo(advertiseHost, port, psk.Value));
    }
#endif
}
