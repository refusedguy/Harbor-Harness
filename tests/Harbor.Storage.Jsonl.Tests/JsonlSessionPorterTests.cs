using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Round-trip contract for <see cref="JsonlSessionPorter" /> — export from a JSONL
///     store, import into a MEMORY store (proves backend-agnostic decode), plus
///     corruption/duplicate-import edge cases.
/// </summary>
public class JsonlSessionPorterTests
{
    private static JsonlSessionPorter CreatePorter()
        => new(NullLogger<JsonlSessionPorter>.Instance);

    private static async Task<JsonlSessionStore> CreateStoreWithFixtureAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-porter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        await store.UpdateAsync(session with { Title = "porter fixture", ParentSessionId = "parent-x" });

        await store.AppendMessageAsync(session.Id, new UserMessage(
            "m-user-1", session.Id, DateTimeOffset.UtcNow.AddSeconds(-2), "list files", "code", "claude-opus-4"));

        await store.AppendMessageAsync(session.Id, new AssistantMessage(
            "m-asst-1", session.Id, DateTimeOffset.UtcNow.AddSeconds(-1),
            [new TextPart("here are the files"), new ThinkingPart("thinking silently")],
            StopReason.Stop, new Usage(10, 5), "claude-opus-4"));

        await store.AppendMessageAsync(session.Id, TestToolResult(session.Id));

        var stats = SessionMetadata.Empty with { Cost = 0.42m, TokensInput = 99, TokensOutput = 11, MessageCount = 3 };
        await store.UpdateStatsAsync(session.Id, stats);

        return store;
    }

    private static ToolResultMessage TestToolResult(string sessionId) => new(
        "m-tool-1", sessionId, DateTimeOffset.UtcNow,
        [new ToolResultEntry("call-1", "ls", "a.txt\nb.txt", false)]);

    private static MemorySessionStore CreateMemoryStore() => new();

    [Test]
    public async Task Export_Import_RoundTripsMessagesMetadataAndLinkage()
    {
        var source = await CreateStoreWithFixtureAsync();
        try
        {
            Session fixture = (await source.ListAsync()).Value[0];
            var porter = CreatePorter();
            var target = CreateMemoryStore();

            var payload = new StringWriter();
            var export = await porter.ExportAsync(source, fixture.Id, payload);
            await Assert.That(export.IsSuccess).IsTrue();

            var import = await porter.ImportAsync(target, new StringReader(payload.ToString()));
            await Assert.That(import.IsSuccess).IsTrue();
            string newId = import.Value;
            await Assert.That(newId).IsNotEqualTo(fixture.Id); // fresh id minted

            var messages = await target.GetMessagesAsync(newId);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(3);
            await Assert.That((messages.Value[0] as UserMessage)!.Content).IsEqualTo("list files");
            await Assert.That(messages.Value[1].Role).IsEqualTo("assistant");
            var assistant = (AssistantMessage)messages.Value[1];
            await Assert.That(assistant.Parts.Count).IsEqualTo(2); // text + thinking survive
            await Assert.That((messages.Value[2] as ToolResultMessage)!.Results[0].Output)
                .IsEqualTo("a.txt\nb.txt");

            var created = await target.GetAsync(newId);
            await Assert.That(created.IsSuccess).IsTrue();
            await Assert.That(created.Value.Title).IsEqualTo("porter fixture");
            await Assert.That(created.Value.ParentSessionId).IsEqualTo("parent-x");

            var stats = await target.GetStatsAsync(newId);
            await Assert.That(stats.IsSuccess).IsTrue();
            // The JSONL SOURCE derives stats from ASSISTANT messages only on read
            // (UpdateStatsAsync is a documented no-op there): 1 assistant msg
            // with in=10 out=5 → those are the exported metadata values.
            await Assert.That(stats.Value.TokensInput).IsEqualTo(10);
            await Assert.That(stats.Value.TokensOutput).IsEqualTo(5);
            await Assert.That(stats.Value.MessageCount).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(source.GetRootDirectory())) Directory.Delete(source.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task Import_Twice_MintsTwoIndependentSessions()
    {
        var source = await CreateStoreWithFixtureAsync();
        try
        {
            Session fixture = (await source.ListAsync()).Value[0];
            var porter = CreatePorter();
            var target = CreateMemoryStore();

            var payload = new StringWriter();
            await porter.ExportAsync(source, fixture.Id, payload);
            string text = payload.ToString();

            var first = await porter.ImportAsync(target, new StringReader(text));
            var second = await porter.ImportAsync(target, new StringReader(text));

            await Assert.That(first.IsSuccess && second.IsSuccess).IsTrue();
            await Assert.That(first.Value).IsNotEqualTo(second.Value);
            var list = await target.ListAsync();
            await Assert.That(list.Value.Count).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(source.GetRootDirectory())) Directory.Delete(source.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task Import_EmptyPayload_FailsExplicitly()
    {
        var porter = CreatePorter();
        var result = await porter.ImportAsync(CreateMemoryStore(), new StringReader(""));
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("empty");
    }

    [Test]
    public async Task Import_ArbitraryText_FailsOnMarker()
    {
        var porter = CreatePorter();
        var result = await porter.ImportAsync(CreateMemoryStore(), new StringReader("{\"unrelated\":true}\n"));
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("$harbor-session-export");
    }

    [Test]
    public async Task Export_MissingSession_FailsVerbatim()
    {
        var porter = CreatePorter();
        var target = CreateMemoryStore();
        var payload = new StringWriter();

        var result = await porter.ExportAsync(target, "no-such-session", payload);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Cannot export");
    }

    [Test]
    public async Task Import_MalformedLine_SkipsButImportsRest()
    {
        var source = await CreateStoreWithFixtureAsync();
        try
        {
            Session fixture = (await source.ListAsync()).Value[0];
            var porter = CreatePorter();
            var target = CreateMemoryStore();

            var payload = new StringWriter();
            await porter.ExportAsync(source, fixture.Id, payload);
            // Corrupt one middle line; a valid header remains line 1.
            string[] lines = payload.ToString().Split('\n');
            lines[lines.Length - 2] = "{this is not json";
            string corrupted = string.Join('\n', lines);

            var import = await porter.ImportAsync(target, new StringReader(corrupted));
            await Assert.That(import.IsSuccess).IsTrue(); // diagnostics-preserving skip policy

            var messages = await target.GetMessagesAsync(import.Value);
            await Assert.That(messages.Value.Count).IsEqualTo(2); // 3 minus the corrupt one
        }
        finally
        {
            if (Directory.Exists(source.GetRootDirectory())) Directory.Delete(source.GetRootDirectory(), true);
        }
    }
}
