using MessagePack;
namespace Harbor.Ipc.Protocol;
/// <summary>
///     Base type for all Harbor IPC responses. The server always echoes the
///     <see cref="RequestId" /> from the originating <see cref="HarborRequest" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Three response shapes:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="OkResponse" /> — success with optional
///             <see cref="OkResponse.Payload" /> (MessagePack-typeless bytes
///             carrying a domain object).
///         </item>
///         <item>
///             <see cref="ErrorResponse" /> — failure with a human-readable
///             error message.
///         </item>
///         <item>
///             <see cref="EventEnvelope" /> — a server-pushed
///             <see cref="HarborEvent" />. Only sent after a
///             <see cref="SubscribeToEventsRequest" />; never in reply to a
///             regular request.
///         </item>
///     </list>
///     <para>
///         Keeping the response union to exactly these three shapes keeps the
///         wire contract trivial for non-.NET clients (Python / JS / Rust / Go):
///         decode the tag, then either unwrap the payload bytes or read the
///         error string.
///     </para>
/// </remarks>
[MessagePackObject]
[Union(0, typeof(OkResponse))]
[Union(1, typeof(ErrorResponse))]
[Union(2, typeof(EventEnvelope))]
public abstract record HarborResponse
{
    /// <summary>
    ///     Echoed from <see cref="HarborRequest.RequestId" />. For
    ///     <see cref="EventEnvelope" /> this is <see cref="Guid.Empty" />
    ///     (server-pushed, no originating request).
    /// </summary>
    [Key(0)]
    public Guid RequestId { get; init; }
}

/// <summary>
///     Success response. <see cref="Payload" /> is a MessagePack-typeless-
///     serialized domain object (or <see langword="null" /> for void methods).
/// </summary>
[MessagePackObject]
public sealed record OkResponse : HarborResponse
{
    /// <summary>
    ///     MessagePack-typeless serialized domain payload. <see langword="null" />
    ///     for void-returning methods (StartAgent, AbortAgent, DeleteSession).
    /// </summary>
    [Key(1)]
    public byte[]? Payload { get; init; }
}

/// <summary>
///     Failure response. <see cref="Message" /> is the user-facing error
///     string from <c>Result.Failure</c>.
/// </summary>
[MessagePackObject]
public sealed record ErrorResponse : HarborResponse
{
    /// <summary>The error message.</summary>
    [Key(1)]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
///     A server-pushed event frame. Sent only after the client opens an event
///     subscription with <see cref="SubscribeToEventsRequest" />.
/// </summary>
/// <remarks>
///     <see cref="Event" /> is MessagePack-serialized using the
///     <c>[Union]</c> tag on <see cref="HarborEvent" />. The wire form of an
///     EventEnvelope is therefore: <c>[tag=2][RequestId=Guid.Empty][event-bytes]</c>.
/// </remarks>
[MessagePackObject]
public sealed record EventEnvelope : HarborResponse
{
    /// <summary>
    ///     MessagePack-serialized <see cref="HarborEvent" /> (union-tagged).
    /// </summary>
    [Key(1)]
    public byte[]? EventBytes { get; init; }
}
