using Harbor.Ipc.Protocol;
namespace Harbor.Ipc;
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
