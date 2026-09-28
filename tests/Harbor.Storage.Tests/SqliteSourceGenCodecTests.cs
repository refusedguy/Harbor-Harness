using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Storage.Tests;
/// <summary>
///     Exhaustive source-gen codec tests for #177 (risky half): every
///     <see cref="AgentMessage" /> subtype must round-trip through the new
///     <see cref="SqliteMappers.SerializeMessage" /> switch and the untouched
///     legacy reader, and the emitted bytes must keep the legacy shape
///     (no <c>$type</c> discriminator, string <c>stopReason</c>) so old rows
///     and new rows decode identically.
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteSourceGenCodecTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static UserMessage NewUser(string id = "u1") =>
        new(id, "sess-1", T0, "hello", "code", "p/m");

    private static AssistantMessage NewAssistant(string id = "a1")
    {
        using var doc = JsonDocument.Parse("""{"path":"README.md","limit":10}""");
        return new AssistantMessage(
            id,
            "sess-1",
            T0.AddSeconds(1),
            [
                new TextPart("here you go"),
                new ThinkingPart("hmm"),
                ToolCallPart.Create("tc-1", "read", doc.RootElement),
                new FilePart("img.png", "image/png", 42, [1, 2, 3]),
            ],
            StopReason.ToolUse,
            new Usage(10, 20),
            "p/m",
            null,
            IsSummary: true,
            SummaryFirstKeptId: "u1");
    }

    private static ToolResultMessage NewToolResult(string id = "t1", object? metadata = null) =>
        new(id, "sess-1", T0.AddSeconds(2),
            [
                new ToolResultEntry("tc-1", "read", "file contents", false, metadata),
                new ToolResultEntry("tc-2", "bash", "boom", true),
            ]);

    [Test]
    public async Task SerializeMessage_UserMessage_RoundTrips()
    {
        var original = NewUser();

        string json = SqliteMappers.SerializeMessage(original);
        var back = SqliteMappers.TryDeserializeMessage("user", json);

        await Assert.That(back.IsSuccess).IsTrue();
        var user = (UserMessage)back.Value;
        await Assert.That(user.Id).IsEqualTo(original.Id);
        await Assert.That(user.SessionId).IsEqualTo(original.SessionId);
        await Assert.That(user.Content).IsEqualTo("hello");
        await Assert.That(user.Agent).IsEqualTo("code");
        await Assert.That(user.Model).IsEqualTo("p/m");
        await Assert.That(user.CreatedAt).IsEqualTo(original.CreatedAt);
    }

    [Test]
    public async Task SerializeMessage_AssistantMessage_AllPartKinds_RoundTrips()
    {
        var original = NewAssistant();

        string json = SqliteMappers.SerializeMessage(original);
        var back = SqliteMappers.TryDeserializeMessage("assistant", json);

        await Assert.That(back.IsSuccess).IsTrue();
        var assistant = (AssistantMessage)back.Value;
        await Assert.That(assistant.Id).IsEqualTo("a1");
        await Assert.That(assistant.Parts.Count).IsEqualTo(4);
        await Assert.That(((TextPart)assistant.Parts[0]).Text).IsEqualTo("here you go");
        await Assert.That(((ThinkingPart)assistant.Parts[1]).Text).IsEqualTo("hmm");
        var toolCall = (ToolCallPart)assistant.Parts[2];
        await Assert.That(toolCall.Id).IsEqualTo("tc-1");
        await Assert.That(toolCall.ToolName).IsEqualTo("read");
        await Assert.That(toolCall.Args.GetProperty("limit").GetInt32()).IsEqualTo(10);
        var file = (FilePart)assistant.Parts[3];
        await Assert.That(file.Path).IsEqualTo("img.png");
        await Assert.That(file.Data is not null && file.Data.SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
        await Assert.That(assistant.StopReason).IsEqualTo(StopReason.ToolUse);
        await Assert.That(assistant.Usage.InputTokens).IsEqualTo(10);
        await Assert.That(assistant.Usage.OutputTokens).IsEqualTo(20);
        await Assert.That(assistant.IsSummary).IsTrue();
        await Assert.That(assistant.SummaryFirstKeptId).IsEqualTo("u1");
    }

    [Test]
    public async Task SerializeMessage_ToolResultMessage_RoundTrips()
    {
        var original = NewToolResult();

        string json = SqliteMappers.SerializeMessage(original);
        var back = SqliteMappers.TryDeserializeMessage("tool_result", json);

        await Assert.That(back.IsSuccess).IsTrue();
        var toolResult = (ToolResultMessage)back.Value;
        await Assert.That(toolResult.Results.Count).IsEqualTo(2);
        await Assert.That(toolResult.Results[0].Output).IsEqualTo("file contents");
        await Assert.That(toolResult.Results[0].IsError).IsFalse();
        await Assert.That(toolResult.Results[1].IsError).IsTrue();
    }

    [Test]
    public async Task SerializeMessage_EmitsNoPolymorphicDiscriminator()
    {
        string json = SqliteMappers.SerializeMessage(NewAssistant());

        // Source-gen polymorphism would leak a $type marker per part; the layered
        // ContentPartJsonConverter must keep the legacy {"type":...} shape.
        await Assert.That(json.Contains("$type")).IsFalse();
        await Assert.That(json.Contains("\"type\":\"tool_call\"")).IsTrue();
    }

    [Test]
    public async Task SerializeMessage_WritesStopReasonAsCamelCaseString()
    {
        string json = SqliteMappers.SerializeMessage(NewAssistant());

        // Legacy shape via JsonStringEnumConverter; a numeric enum would still decode
        // (AllowIntegerValues) but would silently change the stored format.
        await Assert.That(json.Contains("\"stopReason\":\"toolUse\"")).IsTrue();
    }

    [Test]
    public async Task SerializeMessage_StripsNonNullMetadataInsteadOfThrowing()
    {
        var original = NewToolResult(metadata: "sensitive");

        // Must not throw (source-gen cannot serve object?-typed members); the drop
        // mirrors the JSONL codec (#51) and MemoryPack ([MemoryPackIgnore]).
        string json = SqliteMappers.SerializeMessage(original);
        var back = SqliteMappers.TryDeserializeMessage("tool_result", json);

        await Assert.That(back.IsSuccess).IsTrue();
        var toolResult = (ToolResultMessage)back.Value;
        await Assert.That(toolResult.Results[0].Output).IsEqualTo("file contents");
        await Assert.That(toolResult.Results[0].Metadata).IsNull();
        await Assert.That(original.Results[0].Metadata).IsEqualTo("sensitive");
    }

    [Test]
    public async Task Store_AppendAndRead_AllSubtypes_RoundTrip()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"harbor-sqlite-{Guid.NewGuid():N}.db");
        var store = new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "p", "p/m")).Value;
            var user = NewUser() with { SessionId = session.Id };
            var assistant = NewAssistant() with { SessionId = session.Id };
            var toolResult = NewToolResult() with { SessionId = session.Id };

            await store.AppendMessageAsync(session.Id, user);
            await store.AppendMessageAsync(session.Id, assistant);
            await store.AppendMessageAsync(session.Id, toolResult);

            var read = await store.GetMessagesAsync(session.Id);

            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Count).IsEqualTo(3);
            await Assert.That(read.Value[0]).IsTypeOf<UserMessage>();
            await Assert.That(((UserMessage)read.Value[0]).Content).IsEqualTo("hello");
            await Assert.That(read.Value[1]).IsTypeOf<AssistantMessage>();
            await Assert.That(((AssistantMessage)read.Value[1]).Parts.Count).IsEqualTo(4);
            await Assert.That(read.Value[2]).IsTypeOf<ToolResultMessage>();
            await Assert.That(((ToolResultMessage)read.Value[2]).Results.Count).IsEqualTo(2);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
