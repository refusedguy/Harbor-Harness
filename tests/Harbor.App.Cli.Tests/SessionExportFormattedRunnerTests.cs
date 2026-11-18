using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Behavior tests for the Markdown/JSON session export added in #2675 item 049.
///     The export reads the store's message surface and writes a file; it never
///     mutates the session.
/// </summary>
public class SessionExportFormattedRunnerTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly MemorySessionStore _store = new();
    private readonly string _path;

    public SessionExportFormattedRunnerTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"harbor-export-{Guid.NewGuid():N}.out");
    }

    [Test]
    public async Task JsonExport_Contains_Session_Metadata_And_All_Roles()
    {
        var session = await SeedAsync(
            User("what is the answer"),
            Assistant("the answer is 42"),
            ToolResult("computed"));

        int exit = await ExportAsync(session.Id, SessionExportFormat.Json);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(_path)).IsTrue();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("id").GetString()).IsEqualTo(session.Id);
        await Assert.That(root.GetProperty("title").GetString()).IsEqualTo("export me");
        await Assert.That(root.GetProperty("messages").GetArrayLength()).IsEqualTo(3);
        await Assert.That(root.GetProperty("messages")[0].GetProperty("role").GetString()).IsEqualTo("user");
        await Assert.That(root.GetProperty("messages")[0].GetProperty("content").GetString())
            .IsEqualTo("what is the answer");
        await Assert.That(root.GetProperty("messages")[1].GetProperty("role").GetString()).IsEqualTo("assistant");
        await Assert.That(root.GetProperty("messages")[2].GetProperty("role").GetString()).IsEqualTo("tool_result");
    }

    [Test]
    public async Task JsonExport_Writes_ToolCall_Args_And_Result_Output()
    {
        using var argsDoc = JsonDocument.Parse("""{"path":"README.md"}""");
        var call = ToolCallPart.Create("tc1", "read", argsDoc.RootElement);
        var session = await SeedAsync(AssistantWithParts(call), ToolResult("file contents"));

        int exit = await ExportAsync(session.Id, SessionExportFormat.Json);

        await Assert.That(exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        var messages = doc.RootElement.GetProperty("messages");
        var part = messages[0].GetProperty("parts")[0];
        await Assert.That(part.GetProperty("type").GetString()).IsEqualTo("tool_call");
        await Assert.That(part.GetProperty("toolName").GetString()).IsEqualTo("read");
        await Assert.That(part.GetProperty("args").GetProperty("path").GetString()).IsEqualTo("README.md");
        await Assert.That(messages[1].GetProperty("results")[0].GetProperty("output").GetString())
            .IsEqualTo("file contents");
    }

    [Test]
    public async Task MarkdownExport_Contains_Title_And_Roles()
    {
        var session = await SeedAsync(User("hello markdown"), Assistant("hello back"));

        int exit = await ExportAsync(session.Id, SessionExportFormat.Markdown);

        await Assert.That(exit).IsEqualTo(0);
        string text = await File.ReadAllTextAsync(_path);
        await Assert.That(text).Contains("# export me");
        await Assert.That(text).Contains($"- Session: `{session.Id}`");
        await Assert.That(text).Contains("### user");
        await Assert.That(text).Contains("hello markdown");
        await Assert.That(text).Contains("### assistant");
        await Assert.That(text).Contains("hello back");
    }

    [Test]
    public async Task MissingSession_Returns_One_Without_Writing_File()
    {
        int exit = await ExportAsync("missing-session", SessionExportFormat.Json);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(_path)).IsFalse();
        await Assert.That(_err.ToString()).Contains("Export failed");
    }

    [Test]
    public async Task Export_Does_Not_Mutate_The_Store()
    {
        var session = await SeedAsync(User("keep me"));

        await ExportAsync(session.Id, SessionExportFormat.Markdown);

        var messages = await _store.GetMessagesAsync(session.Id);
        await Assert.That(messages.Value.Count).IsEqualTo(1);
        var reloaded = await _store.GetAsync(session.Id);
        await Assert.That(reloaded.Value.Title).IsEqualTo("export me");
    }

    [After(Test)]
    public void Cleanup()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private Task<int> ExportAsync(string sessionId, SessionExportFormat format)
        => SessionExportFormattedRunner.RunAsync(_out, _err, _store, sessionId, _path, format);

    private async Task<Harbor.Abstractions.Models.Session> SeedAsync(params AgentMessage[] messages)
        => await SessionFixtures.SeedAsync(_store, "export me", messages);

    private static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"), "s", DateTimeOffset.UtcNow, content, "code", "test-model");

    private static AssistantMessage Assistant(string text) => AssistantWithParts(new TextPart(text));

    private static AssistantMessage AssistantWithParts(params ContentPart[] parts) => new(
        Guid.NewGuid().ToString("N"),
        "s",
        DateTimeOffset.UtcNow,
        parts,
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    private static ToolResultMessage ToolResult(string output) => new(
        Guid.NewGuid().ToString("N"),
        "s",
        DateTimeOffset.UtcNow,
        [new ToolResultEntry(Guid.NewGuid().ToString("N"), "read", output, false)]);
}
