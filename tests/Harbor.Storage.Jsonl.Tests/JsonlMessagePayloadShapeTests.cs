using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
namespace Harbor.Storage.Jsonl.Tests;
/// <summary>
///     Byte-shape + round-trip tests for the <see cref="JsonlMessageCodec.SerializeMessagePayload" />
///     write path (issue #1531). The slice replaced the per-message
///     <c>Select(...).ToArray()</c> chains and the
///     <c>StopReason.ToString().ToLowerInvariant()</c> call with pre-sized
///     indexed loops and a constant switch — the on-disk payload shapes must
///     stay byte-identical, so these tests pin the exact wire spelling
///     (notably <c>"stopReason":"tooluse"</c>, not <c>"tool_use"</c>, which
///     is a provider-side alias rather than the JSONL spelling) and the
///     round-trip through <see cref="JsonlMessageCodec.DeserializeMessage" />.
/// </summary>
public class JsonlMessagePayloadShapeTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     The same composition the store performs on every append: wrap the
    ///     projected payload in a <see cref="MessageEntry" /> and serialize
    ///     through the generated <see cref="JsonlCodecContext.Default.MessageEntry" />.
    /// </summary>
    private static string EntryJson(AgentMessage message)
    {
        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);
        return Encoding.UTF8.GetString(bytes);
    }

    [Test]
    [Arguments(StopReason.Stop, "stop")]
    [Arguments(StopReason.Length, "length")]
    [Arguments(StopReason.ToolUse, "tooluse")]
    [Arguments(StopReason.ContentFilter, "contentfilter")]
    [Arguments(StopReason.Error, "error")]
    [Arguments(StopReason.Aborted, "aborted")]
    public async Task Serialize_AssistantStopReason_WritesLowercaseWireSpelling(
        StopReason reason, string expected)
    {
        var message = TestMessages.Assistant("hi", id: "m1", sessionId: "sess-1", createdAt: CreatedAt, stopReason: reason, usage: new Usage(10, 5), model: "p/m");

        string json = EntryJson(message);

        await Assert.That(json.Contains($"\"stopReason\":\"{expected}\"", StringComparison.Ordinal))
            .IsTrue();
    }

    [Test]
    public async Task Serialize_AssistantToolUse_NeverWritesToolUseAliasSpelling()
    {
        var message = TestMessages.Assistant("hi", id: "m1", sessionId: "sess-1", createdAt: CreatedAt, stopReason: StopReason.ToolUse, usage: new Usage(1, 1), model: "p/m");

        string json = EntryJson(message);

        // The provider-side alias "tool_use" is accepted on READ, but the on-disk
        // contract of this codec is the lowered enum name — a here-undocumented
        // rename would break byte-compat with files written by older builds.
        await Assert.That(json.Contains("\"stopReason\":\"tooluse\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"stopReason\":\"tool_use\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Serialize_AssistantParts_Empty_NoPartsMemberRegression()
    {
        // Zero parts must still round-trip: the payload's parts array serializes
        // as [] and the message decodes back to an assistant with no parts.
        var message = TestMessages.AssistantParts([], id: "m1", sessionId: "sess-1", createdAt: CreatedAt, model: "p/m");

        string json = EntryJson(message);

        await Assert.That(json.Contains("\"parts\":[]", StringComparison.Ordinal)).IsTrue();

        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var assistant = (AssistantMessage)back.Value;
        await Assert.That(assistant.Parts).IsEmpty();
        await Assert.That(assistant.StopReason).IsEqualTo(StopReason.Stop);
    }

    [Test]
    public async Task Serialize_AssistantParts_OrderAndKindsRoundTrip()
    {
        JsonElement args = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement;
        var message = TestMessages.AssistantParts([new TextPart("thinking-out"), new ThinkingPart("reason"), ToolCallPart.Create("tc_1", "read", args)], id: "m1", sessionId: "sess-1", createdAt: CreatedAt, stopReason: StopReason.ToolUse, usage: new Usage(10, 5), model: "p/m");

        string json = EntryJson(message);

        await Assert.That(json.Contains(
            "\"parts\":[{\"type\":\"text\",\"text\":\"thinking-out\"},{\"type\":\"thinking\",\"text\":\"reason\"},{\"type\":\"tool_call\",\"id\":\"tc_1\",\"toolName\":\"read\",\"args\":{\"path\":\"a.txt\"}}]",
            StringComparison.Ordinal)).IsTrue();

        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var assistant = (AssistantMessage)back.Value;
        await Assert.That(assistant.Parts.Count).IsEqualTo(3);
        await Assert.That(assistant.Parts[0]).IsEqualTo(new TextPart("thinking-out"));
        await Assert.That(assistant.Parts[1]).IsEqualTo(new ThinkingPart("reason"));
        var toolCall = (ToolCallPart)assistant.Parts[2];
        await Assert.That(toolCall.Id).IsEqualTo("tc_1");
        await Assert.That(toolCall.ToolName).IsEqualTo("read");
        await Assert.That(toolCall.Args.GetProperty("path").GetString()).IsEqualTo("a.txt");
    }

    [Test]
    public async Task Serialize_ToolResult_EntriesOrderPreserved_AndMetadataDropped()
    {
        var message = TestMessages.ToolResult([new ToolResultEntry("tc_1", "read", "first", IsError: false, Metadata: new object()), new ToolResultEntry("tc_2", "bash", "second", IsError: true),], id: "m2", sessionId: "sess-1", createdAt: CreatedAt);

        string json = EntryJson(message);

        // The entry projection used to ride the same Select(...) chain, so both
        // contracts are re-pinned here. Each entry's own spelling is asserted
        // (not one concatenated literal), the two must appear in message order,
        // and no metadata content may survive a non-null one.
        await Assert.That(json.Contains(
            "{\"toolCallId\":\"tc_1\",\"toolName\":\"read\",\"output\":\"first\",\"isError\":false",
            StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains(
            "{\"toolCallId\":\"tc_2\",\"toolName\":\"bash\",\"output\":\"second\",\"isError\":true",
            StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.IndexOf("tc_1", StringComparison.Ordinal))
            .IsLessThan(json.IndexOf("tc_2", StringComparison.Ordinal));

        // #51 drops Metadata's CONTENT, not the member. The projection writes
        // entries whose Metadata is null through `JsonlCodecContext.Default.*`,
        // whose generated type info does not carry the WhenWritingNull rule —
        // that lives only on JsonlCodecContext.JsonOptions — so the wire
        // spelling of both entries is `"metadata":null` (byte-compat with older
        // builds, which wrote the same null member). What must never survive is
        // a non-null value, asserted per entry below rather than against the
        // raw string, because the content itself is arbitrary `object?`.
        await Assert.That(json.Contains("\"metadata\":null", StringComparison.Ordinal)).IsTrue();

        using var doc = JsonDocument.Parse(json);
        foreach (var entryEl in doc.RootElement.GetProperty("payload").GetProperty("results").EnumerateArray())
        {
            bool metadataIsNull = !entryEl.TryGetProperty("metadata", out var m) || m.ValueKind == JsonValueKind.Null;
            await Assert.That(metadataIsNull).IsTrue();
        }

        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var toolResult = (ToolResultMessage)back.Value;
        await Assert.That(toolResult.Results.Count).IsEqualTo(2);
        await Assert.That(toolResult.Results[0].Output).IsEqualTo("first");
        await Assert.That(toolResult.Results[0].IsError).IsFalse();
        await Assert.That(toolResult.Results[1].Output).IsEqualTo("second");
        await Assert.That(toolResult.Results[1].IsError).IsTrue();
    }

    [Test]
    public async Task Serialize_ToolResult_EmptyResults_RoundTrips()
    {
        var message = TestMessages.ToolResult([], id: "m2", sessionId: "sess-1", createdAt: CreatedAt);

        string json = EntryJson(message);

        await Assert.That(json.Contains("\"results\":[]", StringComparison.Ordinal)).IsTrue();

        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        await Assert.That(((ToolResultMessage)back.Value).Results).IsEmpty();
    }
}
