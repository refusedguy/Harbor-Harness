using System.Threading.Channels;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     The WIRE-LEVEL RPC client contract — one connected MessagePack channel:
///     request/response, the out-of-band event stream, and a signal for the
///     read loop dying. Implemented by <c>MessagePackRpcClient</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not <see cref="IHarborClient" /> (issue #494).</b>
///         <c>IHarborClient</c> is the DOMAIN contract every UI layer talks to,
///         and it is deliberately lossy about the wire. Three things a
///         reconnecting client must know are not expressible through it:
///     </para>
///     <list type="number">
///         <item>
///             <b>Sequence numbers.</b> <c>SubscribeToEventsAsync</c> yields
///             <see cref="HarborEvent" />, which carries no sequence. The
///             reconnect protocol is a SEQUENCE protocol — <c>lastSeen</c>,
///             resume-from-<c>lastSeen</c>, duplicate suppression and the
///             subscribe-before-snapshot baseline all compare
///             <see cref="EventFrame.Sequence" />, a field
///             <see cref="HarborEvent" /> does not have.
///         </item>
///         <item>
///             <b>The ack's own shape.</b> The subscribe path must branch on
///             <c>OkResponse</c> vs <c>ErrorResponse</c> and read
///             <c>OkResponse.Payload</c> as <c>SubscriptionAck</c> bytes.
///             <c>IHarborClient</c>'s methods return <c>Result&lt;…&gt;</c>,
///             which collapses exactly that distinction — and a protocol-level
///             refusal (PSK gate, unknown request) is not a transient failure to
///             retry, it is a reason to fail loudly.
///         </item>
///         <item>
///             <b>An edge, not a level.</b> A reconnecting pump needs
///             "the connection just died" to break out of its read loop now.
///             <c>IsConnected</c> is a level and cannot carry an edge; polling it
///             would race the disposal that produced it.
///         </item>
///     </list>
///     <para>
///         So this interface is deliberately SMALLER than
///         <see cref="IHarborClient" /> (four members, not fifteen) and sits a
///         layer below it. That is the opposite of an ISP violation: the
///         decorator needs five facts and gets exactly five, and a fake
///         implementing them needs no pipes, no MessagePack and no server.
///     </para>
/// </remarks>
public interface IRpcClient : IAsyncDisposable
{
    /// <summary>
    ///     Reader side of the event channel. Frames carry the server-assigned
    ///     envelope sequence so reconnecting clients can dedup and bookkeep.
    /// </summary>
    public ChannelReader<EventFrame> EventFrames { get; }

    /// <summary>
    ///     Raised when the read loop dies from EOF/IO error (NOT on Dispose).
    ///     Reconnecting callers use this as the "dial again" trigger.
    /// </summary>
    public event EventHandler ConnectionLost;

    /// <summary>
    ///     Connect the underlying channel and start the background read loop.
    ///     Idempotent — calling twice is a no-op.
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    ///     Send a request and await the matching response.
    /// </summary>
    public Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default);
}