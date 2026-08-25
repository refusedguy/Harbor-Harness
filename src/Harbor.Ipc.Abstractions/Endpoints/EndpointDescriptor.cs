using MessagePack;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     Where an IPC endpoint lives. Resolved from
///     <c>~/.harbor/hosts.json</c> (name → descriptor) by
///     <see cref="HostsCatalog" />; clients accept a host name and talk to
///     whatever the catalog points at.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kinds:</b>
///     </para>
///     <list type="bullet">
///         <item><see cref="Uds"/> — local Unix domain socket / named pipe (same machine only).</item>
///         <item><see cref="Tcp"/> — raw TCP host:port (LAN address, explicit IP, DNS name).</item>
///         <item><see cref="Tailscale"/> — TCP to a tailnet peer: a MagicDNS name or 100.x CGNAT
///             address. Reachable from anywhere in the tailnet, never from the public internet;
///             PSK handshake is mandatory on these listeners (defence in depth).</item>
///     </list>
///     <para>
///         Config-only vocabulary — never serialized to the RPC wire.
///     </para>
/// </remarks>
public abstract record EndpointDescriptor
{
    /// <summary>Local socket: pipe name (Windows) or socket path (Unix).</summary>
    public sealed record Uds(string Path) : EndpointDescriptor;

    /// <summary>TCP endpoint on a LAN host, IP, or any resolvable DNS name.</summary>
    /// <remarks><see cref="Psk"/> carries the optional pre-shared key copied
    /// from hosts.json so tools like <c>harbor status --all</c> can
    /// authenticate probes without extra plumbing.</remarks>
    public sealed record Tcp(string Host, int Port) : EndpointDescriptor
    {
        /// <summary>Optional pre-shared key for PSK-gated listeners.</summary>
        public string? Psk { get; init; }
    }

    /// <summary>
    ///     Tailscale peer. <paramref name="Name"/> is the MagicDNS name (or
    ///     bare tailnet hostname); when <paramref name="Host"/> is set it is
    ///     used verbatim as the connect target, otherwise the name itself is.
    /// </summary>
    public sealed record Tailscale(string Name, string? Host, int Port) : EndpointDescriptor
    {
        /// <summary>The host actually dialed: explicit override or the MagicDNS name.</summary>
        public string ConnectHost => string.IsNullOrWhiteSpace(Host) ? Name : Host!;

        /// <summary>Optional pre-shared key for PSK-gated listeners.</summary>
        public string? Psk { get; init; }
    }
}
