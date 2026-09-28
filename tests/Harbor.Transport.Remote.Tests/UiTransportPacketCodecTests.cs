using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Transport.Remote;
using TUnit.Assertions;

namespace Harbor.Transport.Remote.Tests;

/// <summary>
///     Wire-format contract for <see cref="RemoteClient" /> (#177 safe half):
///     the packet the client sends must round-trip through the
///     source-generated <see cref="RemoteJsonContext" /> — no reflection
///     fallback, polymorphic event payloads intact.
/// </summary>
public class UiTransportPacketCodecTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Packet_WithPolymorphicEvent_RoundTrips()
    {
        var packet = new UiTransportPacket(
            Type: "turn_start",
            Event: new TurnStartEvent(TurnIndex: 1, SessionId: "s-1"),
            Timestamp: FixedTimestamp);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, RemoteJsonContext.Default.UiTransportPacket);
        await Assert.That(bytes.Length > 0).IsTrue();

        var decoded = JsonSerializer.Deserialize(bytes, RemoteJsonContext.Default.UiTransportPacket);

        await Assert.That(decoded is not null).IsTrue();
        await Assert.That(decoded!.Type).IsEqualTo("turn_start");
        await Assert.That(decoded.Timestamp).IsEqualTo(FixedTimestamp);
        await Assert.That(decoded.Event is TurnStartEvent).IsTrue();
        var turnStart = (TurnStartEvent)decoded.Event!;
        await Assert.That(turnStart.TurnIndex).IsEqualTo(1);
        await Assert.That(turnStart.SessionId).IsEqualTo("s-1");
    }

    [Test]
    public async Task Packet_WithNullEvent_RoundTrips()
    {
        var packet = new UiTransportPacket(
            Type: "heartbeat",
            Event: null,
            Timestamp: FixedTimestamp);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, RemoteJsonContext.Default.UiTransportPacket);
        var decoded = JsonSerializer.Deserialize(bytes, RemoteJsonContext.Default.UiTransportPacket);

        await Assert.That(decoded is not null).IsTrue();
        await Assert.That(decoded!.Type).IsEqualTo("heartbeat");
        await Assert.That(decoded.Event is null).IsTrue();
        await Assert.That(decoded.Timestamp).IsEqualTo(FixedTimestamp);
    }

    [Test]
    public async Task FromEvent_Packet_PreservesTypeAndPayload()
    {
        var packet = UiTransportPacket.FromEvent(new TurnStartEvent(TurnIndex: 2, SessionId: "s-2"));

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, RemoteJsonContext.Default.UiTransportPacket);
        var decoded = JsonSerializer.Deserialize(bytes, RemoteJsonContext.Default.UiTransportPacket);

        await Assert.That(decoded is not null).IsTrue();
        await Assert.That(decoded!.Type).IsEqualTo(nameof(TurnStartEvent));
        await Assert.That(decoded.Event is TurnStartEvent).IsTrue();
        await Assert.That(((TurnStartEvent)decoded.Event!).SessionId).IsEqualTo("s-2");
    }
}
