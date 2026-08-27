using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

public class SessionSearchRunnerTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly MemorySessionStore _store = new();

    private static async Task<Harbor.Abstractions.Models.Session> SeedAsync(
        MemorySessionStore store, string title, params AgentMessage[] messages)
    {
        var created = (await store.CreateAsync("/tmp", "code", "test", "test-model")).Value;
        if (!string.IsNullOrEmpty(title))
        {
            created = created with { Title = title };
            await store.UpdateAsync(created);
        }

        foreach (var message in messages)
        {
            await store.AppendMessageAsync(created.Id, message with { SessionId = created.Id });
        }

        return created;
    }

    private static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"), "s", DateTimeOffset.UtcNow, content, "code", "test-model");

    private static AssistantMessage Assistant(params string[] parts) => new(
        Guid.NewGuid().ToString("N"),
        "s",
        DateTimeOffset.UtcNow,
        [.. parts.Select(p => (ContentPart)new TextPart(p))],
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    private static ToolResultMessage ToolResult(string output) => new(
        Guid.NewGuid().ToString("N"),
        "s",
        DateTimeOffset.UtcNow,
        [new ToolResultEntry(Guid.NewGuid().ToString("N"), "read", output, false)]);

    private Task<int> SearchAsync(string query, string? filter = null)
        => SessionSearchRunner.RunAsync(_out, _err, _store, query, filter);

    [Test]
    public async Task Finds_UserMessage_Match_With_Snippet()
    {
        var session = await SeedAsync(_store, "debug session", User("cannot compile the widget today"));

        int exit = await SearchAsync("compile");

        await Assert.That(exit).IsEqualTo(0);
        string output = _out.ToString();
        await Assert.That(output).Contains(session.Id);
        await Assert.That(output).Contains("[debug session]");
        await Assert.That(output).Contains("user");
        await Assert.That(output).Contains("…cannot compile the widget today…");
    }

    [Test]
    public async Task Match_Is_CaseInsensitive()
    {
        await SeedAsync(_store, string.Empty, User("ALL CAPS PAYLOAD"));

        int exit = await SearchAsync("caps payload");

        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task Joins_Assistant_TextParts_And_Finds_Second_Part()
    {
        await SeedAsync(_store, string.Empty, Assistant("intro text", "needle is here"));

        int exit = await SearchAsync("needle");

        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task Ignores_Thinking_Only_Assistant()
    {
        var thinkingOnly = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            "s",
            DateTimeOffset.UtcNow,
            [new ThinkingPart("hidden deliberation about zebras")],
            StopReason.Stop,
            new Usage(0, 0),
            "test-model");
        await SeedAsync(_store, string.Empty, thinkingOnly);

        int exit = await SearchAsync("zebras");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_out.ToString()).Contains("No matches");
    }

    [Test]
    public async Task Searches_ToolResult_Output()
    {
        await SeedAsync(_store, string.Empty, ToolResult("line1\nstack overflow in module x\nline3"));

        int exit = await SearchAsync("overflow");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString()).Contains("tool_result");
    }

    [Test]
    public async Task SessionFilter_Limits_Scan()
    {
        var withMatch = await SeedAsync(_store, "has it", User("unique-marble-query"));
        await SeedAsync(_store, "also has", User("unique-marble-query"));

        int exit = await SearchAsync("unique-marble-query", filter: withMatch.Id);

        await Assert.That(exit).IsEqualTo(0);
        // Only the filtered session's header appears.
        await Assert.That(CountOccurrences(_out.ToString(), "unique-marble-query") - 1).IsEqualTo(1); // 1 line + 1 match-count summary mention removed → 1 body match
    }

    [Test]
    public async Task NoMatches_Returns_One_And_Reports()
    {
        await SeedAsync(_store, "empty-ish", User("nothing special"));

        int exit = await SearchAsync("quantum-xylophone");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_out.ToString()).Contains("No matches for 'quantum-xylophone'");
    }

    [Test]
    public async Task Empty_Query_Is_Argument_Error()
    {
        int exit = await SearchAsync("");
        await Assert.That(exit).IsEqualTo(2);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
