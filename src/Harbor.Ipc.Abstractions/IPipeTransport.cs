using Harbor.Ipc.Protocol;
namespace Harbor.Ipc;
using System.Threading.Channels;
/// <summary>
///     Bidirectional transport abstraction for the IPC layer. One
///     implementation lives in <c>Harbor.Ipc.Server</c> (accepts inbound
///     connections) and a parallel one lives in <c>Harbor.Ipc.Client</c>
///     (opens an outbound connection).
/// </summary>
/// <remarks>
///     <para>
///         The transport is intentionally tiny — just a duplex
///         <see cref="Stream" /> plus a <see cref="Endpoint" /> string for
///         diagnostics. All framing/MessagePack logic lives in
///         <see cref="WireCodec" />.
///     </para>
///     <para>
///         <b>Two built-in transports:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <c>NamedPipeTransport</c> — Windows. Pipe name like
///             <c>harbor-ipc</c> at <c>\\.\pipe\harbor-ipc</c>.
///         </item>
///         <item>
///             <c>UnixDomainSocketTransport</c> — Linux/Mac. Socket path like
///             <c>/tmp/harbor-ipc.sock</c>.
///         </item>
///     </list>
///     <para>
///         Both transports expose the same shape so the RPC layer is OS-agnostic.
///     </para>
/// </remarks>
public interface IPipeTransport : IAsyncDisposable
{
    /// <summary>The human-readable endpoint (pipe path / socket path / port).</summary>
    public string Endpoint { get; }

    /// <summary>True when the transport is currently bound and accepting connections.</summary>
    public bool IsBound { get; }
}

/// <summary>
///     Client-side dialing contract: opens an outbound <see cref="Stream" />
///     to a daemon endpoint. Implemented by
///     <see cref="ClientPipeTransport"/> (named pipe / UDS) and
///     <see cref="TcpClientTransport"/> (TCP / tailscale) — the RPC client
///     is transport-agnostic above this seam.
/// </summary>
public interface IIpcClientTransport : IPipeTransport
{
    /// <summary>Open (or return the existing healthy) connection stream.</summary>
    Task<Stream> ConnectAsync(CancellationToken ct = default);

    /// <summary>Close the current connection; the next ConnectAsync dials afresh.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
}

/// <summary>
///     Server-side listening contract: binds an endpoint and yields accepted
///     connection streams. Implemented by
///     <see cref="ServerPipeTransport"/> (named pipe / UDS) and
///     <see cref="TcpServerTransport"/> (TCP / tailscale).
/// </summary>
public interface IIpcServerTransport : IPipeTransport
{
    /// <summary>Bind and begin accepting; returns the reader of accepted streams.</summary>
    Task<ChannelReader<Stream>> BindAsync(CancellationToken ct = default);

    /// <summary>Stop accepting, drain, release the endpoint.</summary>
    Task UnbindAsync(CancellationToken ct = default);
}
