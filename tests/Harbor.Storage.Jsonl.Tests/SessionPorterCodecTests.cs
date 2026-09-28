using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using TUnit.Assertions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Codec contract for the porter wire lines (#177 safe half): the export
///     envelope and message entries must round-trip through the TYPED
///     <c>JsonlCodecContext.Default.*</c> entries (AOT-safe, no reflection),
///     the same entries the store itself persists with.
/// </summary>
public class SessionPorterCodecTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Envelope_RoundTripsThroughTypedContext()
    {
        var session = Session.Create("/proj", "code", "anthropic", "claude-opus-4", "codec fixture");
        var envelope = new ExportEnvelope(
            Marker: "$harbor-session-export",
            Version: 1,
            Session: session,
            Metadata: SessionMetadata.Empty);

        string line = JsonSerializer.Serialize(envelope, JsonlCodecContext.Default.ExportEnvelope);
        var decoded = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.ExportEnvelope);

        await Assert.That(decoded is not null).IsTrue();
        await Assert.That(decoded!.Marker).IsEqualTo("$harbor-session-export");
        await Assert.That(decoded.Version).IsEqualTo(1);
        await Assert.That(decoded.Session.Id).IsEqualTo(session.Id);
        await Assert.That(decoded.Session.Title).IsEqualTo("codec fixture");
        await Assert.That(decoded.Metadata is not null).IsTrue();
    }

    [Test]
    public async Task MessageEntry_WithUserPayload_RoundTripsThroughTypedContext()
    {
        var entry = new MessageEntry(
            Type: "message",
            Id: "m-1",
            ParentId: null,
            Role: "user",
            CreatedAt: FixedTimestamp,
            Payload: new UserPayload(Content: "hello", Agent: "code", Model: "claude-opus-4"));

        string line = JsonSerializer.Serialize(entry, JsonlCodecContext.Default.MessageEntry);
        var decoded = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.MessageEntry);

        await Assert.That(decoded is not null).IsTrue();
        await Assert.That(decoded!.Type).IsEqualTo("message");
        await Assert.That(decoded.Id).IsEqualTo("m-1");
        await Assert.That(decoded.Role).IsEqualTo("user");
        await Assert.That(decoded.CreatedAt).IsEqualTo(FixedTimestamp);

        // Payload is object-typed on the wire: re-encoding the decoded entry
        // must preserve the payload content regardless of its runtime type.
        string reencoded = JsonSerializer.Serialize(decoded, JsonlCodecContext.Default.MessageEntry);
        await Assert.That(reencoded.Contains("hello")).IsTrue();
    }
}
