using MessagePack;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     Payload of the SubscribeToEvents ack (carried in the OkResponse
///     payload bytes): the server's current envelope sequence and whether
///     the client must rebuild its view from a fresh snapshot.
/// </summary>
[MessagePackObject]
public sealed record SubscriptionAck(
    [property: Key(0)] ulong ServerSequence,
    [property: Key(1)] bool ResyncRequired)
{
    /// <summary>Parameterless ctor for MessagePack deserialization.</summary>
    public SubscriptionAck() : this(0, false) { }
}
