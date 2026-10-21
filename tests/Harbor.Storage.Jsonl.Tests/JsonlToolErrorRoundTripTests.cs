using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #1760: the structured tool-error code survives the JSONL round-trip.
/// </summary>
/// <remarks>
///     <para>
///         The code is the point of the change, and <c>Metadata</c> — the field
///         that used to be the only place to put anything — is dropped by this
///         codec on purpose (#51). A code parked there would not survive a
///         restart, so it got its own typed field, and this test pins that the
///         field really does survive.
///     </para>
///     <para>
///         The other half is backward compatibility: a session file written
///         before the code existed has no such member at all, and reading it
///         must not fail.
///     </para>
/// </remarks>
public class JsonlToolErrorRoundTripTests
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
    public async Task CodedError_RoundTrips_ThroughTheElementReader()
    {
        var message = TestMessages.ToolResult(
        [
            new ToolResultEntry("tc1", "webfetch", "TOOL_TIMEOUT: Tool 'webfetch' timed out after 30s.", true)
            {
                ErrorInfo = ToolErrorCodes.Create(ToolErrorCodes.Timeout, "deadline 30000ms"),
            },
        ], id: "m1", sessionId: "sess-1", createdAt: CreatedAt);

        string json = EntryJson(message);

        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var results = ((ToolResultMessage)back.Value).Results;
        await Assert.That(results.Count).IsEqualTo(1);
        ToolErrorInfo error = results[0].ErrorInfo!;
        await Assert.That(error.Code).IsEqualTo(ToolErrorCodes.Timeout);
        await Assert.That(error.Name).IsEqualTo("ToolTimeoutError");
        await Assert.That(error.Reason).IsEqualTo("deadline 30000ms");
        await Assert.That(results[0].Output).IsEqualTo("TOOL_TIMEOUT: Tool 'webfetch' timed out after 30s.");
    }

    [Test]
    public async Task CodedError_RoundTrips_ThroughTheLineReader()
    {
        // The streaming reader is a SECOND implementation of the same shape —
        // a hand-rolled Utf8JsonReader walk. Both have to agree, or a session
        // file would read differently depending on which path loaded it.
        var message = TestMessages.ToolResult(
        [
            new ToolResultEntry("tc1", "webfetch", "TOOL_TIMEOUT: timed out", true)
            {
                ErrorInfo = ToolErrorCodes.Create(ToolErrorCodes.Timeout, "deadline 30000ms"),
            },
        ], id: "m1", sessionId: "sess-1", createdAt: CreatedAt);

        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(entry, JsonlCodecContext.Default.MessageEntry);

        var parsed = JsonlLineParser.Parse(line, "sess-1");

        await Assert.That(parsed.IsSuccess).IsTrue();
        var results = ((ToolResultMessage)parsed.Value).Results;
        ToolErrorInfo error = results[0].ErrorInfo!;
        await Assert.That(error.Code).IsEqualTo(ToolErrorCodes.Timeout);
        await Assert.That(error.Reason).IsEqualTo("deadline 30000ms");
    }

    [Test]
    public async Task UnknownCode_RoundTrips_Verbatim()
    {
        // A code this build does not know is still a code: it is carried, not
        // rewritten into a known one and not dropped.
        const string unknown = "PLUGIN_TOOL_QUOTA_EXCEEDED";
        var message = TestMessages.ToolResult(
        [
            new ToolResultEntry("tc1", "plugin", $"{unknown}: quota spent", true)
            {
                ErrorInfo = new ToolErrorInfo("PluginQuotaError", unknown, "quota"),
            },
        ], id: "m1", sessionId: "sess-1", createdAt: CreatedAt);

        string json = EntryJson(message);

        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var entry = ((ToolResultMessage)back.Value).Results[0];
        ToolErrorInfo error = entry.ErrorInfo!;
        await Assert.That(error.Code).IsEqualTo(unknown);
        await Assert.That(error.Name).IsEqualTo("PluginQuotaError");
        await Assert.That(ToolErrorCodes.IsKnown(error.Code)).IsFalse();
    }

    [Test]
    public async Task SuccessEntry_WritesNoErrorMember()
    {
        // The member is omitted when there is nothing to say: a session file
        // full of successful tool results must not grow a null member per
        // call.
        var message = TestMessages.ToolResult(
        [
            new ToolResultEntry("tc1", "read", "file contents", false),
        ], id: "m1", sessionId: "sess-1", createdAt: CreatedAt);

        string json = EntryJson(message);

        await Assert.That(json.Contains("\"errorInfo\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ALineWrittenBeforeTheCodeExisted_StillReads()
    {
        // Backward compatibility: a session file written by an older build has
        // no error member at all. Reading it must succeed, and the entry must
        // come back with no code — an absent member is the honest reading of a
        // record that never carried one.
        byte[] line = Encoding.UTF8.GetBytes(
            "{\"type\":\"message\",\"id\":\"m1\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"role\":\"tool_result\","
            + "\"payload\":{\"results\":[{\"toolCallId\":\"tc1\",\"toolName\":\"read\","
            + "\"output\":\"file contents\",\"isError\":false}]}}");

        var parsed = JsonlLineParser.Parse(line, "sess-1");

        await Assert.That(parsed.IsSuccess).IsTrue();
        var entry = ((ToolResultMessage)parsed.Value).Results[0];
        await Assert.That(entry.Output).IsEqualTo("file contents");
        await Assert.That(entry.ErrorInfo).IsNull();
    }
}
