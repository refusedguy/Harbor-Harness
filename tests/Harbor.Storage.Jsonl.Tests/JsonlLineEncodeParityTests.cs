using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Byte-for-byte parity of the JSONL line encoder.
/// </summary>
/// <remarks>
///     <para>
///         The encoder used to ask the serializer for the payload bytes and
///         then copy them into a second array, one byte longer, to carry the
///         newline. It now writes the payload into a pooled buffer and
///         copies it ONCE, into the array that goes to disk — the same
///         source-generated type info, the same encoder, the same writer
///         options.
///     </para>
///     <para>
///         The claim under test is that the BYTES did not move. The
///         reference is the serializer's own output
///         (<c>JsonSerializer.SerializeToUtf8Bytes</c> with the same
///         type info) plus one LF — which is exactly what the old encoder
///         produced, and what <c>JsonlAppendEncodingTests</c> pins the
///         on-disk file against.
///     </para>
///     <para>
///         All three payload kinds are covered, because the payload rides
///         the entry as an <c>object</c>: the user payload is a flat record,
///         the assistant payload carries an <c>object[]</c> of parts, and the
///         tool-result payload carries an array of entries. The polymorphic
///         property is where a different writer could have produced
///         different bytes.
///     </para>
/// </remarks>
public class JsonlLineEncodeParityTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MessageEntry Entry(AgentMessage message) =>
        new(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));

    [Test]
    public async Task EncodeLine_UserPayload_BytesMatchSourceGenPlusNewline()
    {
        var message = TestMessages.User("hello", id: "m1", sessionId: "sess", createdAt: CreatedAt, model: "p/m");
        await AssertBytesMatchSourceGen(Entry(message));
    }

    [Test]
    public async Task EncodeLine_AssistantPayload_BytesMatchSourceGenPlusNewline()
    {
        using var args = JsonDocument.Parse("""{"path":"a.txt"}""");
        var message = TestMessages.AssistantParts(
            [new TextPart("hi"), new ThinkingPart("reason"), ToolCallPart.Create("tc_1", "read", args.RootElement)],
            id: "a1",
            sessionId: "sess",
            createdAt: CreatedAt,
            stopReason: StopReason.ToolUse,
            usage: new Usage(10, 5),
            model: "p/m");
        await AssertBytesMatchSourceGen(Entry(message));
    }

    [Test]
    public async Task EncodeLine_ToolResultPayload_BytesMatchSourceGenPlusNewline()
    {
        var message = TestMessages.ToolResult(
            [new ToolResultEntry("tc_1", "read", "output", IsError: false)],
            id: "r1",
            sessionId: "sess",
            createdAt: CreatedAt);
        await AssertBytesMatchSourceGen(Entry(message));
    }

    /// <summary>
    ///     The line is the serializer's own bytes plus one LF — byte for
    ///     byte, and with no CR anywhere in it.
    /// </summary>
    private static async Task AssertBytesMatchSourceGen(MessageEntry entry)
    {
        byte[] line = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);

        await Assert.That(line.Length).IsEqualTo(payload.Length + 1);
        await Assert.That(line.AsSpan(0, payload.Length).SequenceEqual(payload)).IsTrue();
        await Assert.That(line[^1]).IsEqualTo((byte)'\n');
        await Assert.That(line.AsSpan(0, payload.Length).IndexOf((byte)'\r')).IsEqualTo(-1);
    }
}
