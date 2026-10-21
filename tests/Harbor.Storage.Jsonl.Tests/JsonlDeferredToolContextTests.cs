using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #1763: the deferred context a tool handed over travels with the result
///     it belongs to, so a session that is reloaded still carries it — and a
///     session written before the feature keeps the byte-identical shape it
///     had.
/// </summary>
public class JsonlDeferredToolContextTests
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

    private static ToolResultMessage MessageWith(ToolResultEntry entry) =>
        TestMessages.ToolResult([entry], id: "m2", sessionId: "sess-1", createdAt: CreatedAt);

    [Test]
    public async Task Serialize_ToolResultWithDeferredContext_WritesTheContext()
    {
        var entry = new ToolResultEntry("tc_1", "linter", "3 warnings", false)
        {
            DeferredContexts =
            [
                new DeferredToolContext("file a.cs: 3 warnings", "linter")
            ]
        };

        string json = EntryJson(MessageWith(entry));

        await Assert.That(json.Contains("\"deferredContexts\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"source\":\"linter\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("file a.cs: 3 warnings", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Serialize_ToolResultWithoutDeferredContext_KeepsTheOldShape()
    {
        // The member is omitted when there is nothing to write, so a session
        // file written before #1763 keeps the bytes it had.
        var entry = new ToolResultEntry("tc_1", "linter", "3 warnings", false);

        string json = EntryJson(MessageWith(entry));

        await Assert.That(json.Contains("\"deferredContexts\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task RoundTrip_ToolResultWithDeferredContext_KeepsTheContext()
    {
        var entry = new ToolResultEntry("tc_1", "linter", "3 warnings", false)
        {
            DeferredContexts =
            [
                new DeferredToolContext(
                    "file a.cs: 3 warnings",
                    "linter",
                    [new DeferredContextMetadata("kind", "diagnostics")])
            ]
        };

        string json = EntryJson(MessageWith(entry));
        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var message = (ToolResultMessage)back.Value;
        var decoded = message.Results.Single().DeferredContexts;
        await Assert.That(decoded).IsNotNull();
        var contexts = decoded!;
        await Assert.That(contexts.Count).IsEqualTo(1);
        await Assert.That(contexts[0].Content).IsEqualTo("file a.cs: 3 warnings");
        await Assert.That(contexts[0].Source).IsEqualTo("linter");
        await Assert.That(contexts[0].Metadata).IsNotNull();
        var metadata = contexts[0].Metadata!;
        await Assert.That(metadata.Single().Key).IsEqualTo("kind");
        await Assert.That(metadata.Single().Value).IsEqualTo("diagnostics");
    }

    [Test]
    public async Task RoundTrip_ToolResultWithAnEmptyDeferredContext_KeepsThePromise()
    {
        // The empty list is the promise: "a context was coming and it did
        // not arrive". It must not decay into "no promise" on the way
        // through the store, or the miss would be invisible after a reload.
        var entry = new ToolResultEntry("tc_1", "linter", "3 warnings", false)
        {
            DeferredContexts = Array.Empty<DeferredToolContext>()
        };

        string json = EntryJson(MessageWith(entry));
        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var message = (ToolResultMessage)back.Value;
        var decoded = message.Results.Single().DeferredContexts;
        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RoundTrip_ToolResultWithoutDeferredContext_KeepsTheAbsence()
    {
        var entry = new ToolResultEntry("tc_1", "linter", "3 warnings", false);

        string json = EntryJson(MessageWith(entry));
        using var doc = JsonDocument.Parse(json);
        var back = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(back.IsSuccess).IsTrue();
        var message = (ToolResultMessage)back.Value;
        await Assert.That(message.Results.Single().DeferredContexts).IsNull();
    }
}
