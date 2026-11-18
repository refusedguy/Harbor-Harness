using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Storage.Memory;
using Harbor.TestKit;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Behavior tests for on-demand title regeneration (issue #2684 S06).
///     The naming call is scripted, so the tests prove what reaches the model
///     and that only the title changes in the store.
/// </summary>
public class SessionRetitleRunnerTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly MemorySessionStore _store = new();

    private static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"), "s", DateTimeOffset.UtcNow, content, "code", "test-model");

    private async Task<string> SeedAsync(string title = "old title")
    {
        var session = await SessionFixtures.SeedAsync(_store, title, User("Please name this session"));
        return session.Id;
    }

    private Task<int> RunAsync(string id, ScriptedLlmClient llm)
        => SessionRetitleRunner.RunAsync(_out, _err, _store, new FakeProviderRegistry(llm), id);

    [Test]
    public async Task Regenerates_Title_And_Leaves_Messages_Alone()
    {
        string id = await SeedAsync();
        var llm = new ScriptedLlmClient(new LlmEvent[] { new TextDeltaEvent("d1", "  \"Brisk Naming\"  ") });

        int exit = await RunAsync(id, llm);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That((await _store.GetAsync(id)).Value.Title).IsEqualTo("Brisk Naming");
        await Assert.That((await _store.GetMessagesAsync(id)).Value.Count).IsEqualTo(1);
        await Assert.That(llm.Requests.Count).IsEqualTo(1);
        await Assert.That(llm.Requests[0].Tools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Around_Quotes_Are_Stripped_From_The_Model_Answer()
    {
        string id = await SeedAsync();
        var llm = new ScriptedLlmClient(new LlmEvent[] { new TextDeltaEvent("d1", "\"Quoted Title\"") });

        int exit = await RunAsync(id, llm);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That((await _store.GetAsync(id)).Value.Title).IsEqualTo("Quoted Title");
    }

    [Test]
    public async Task Empty_Model_Answer_Keeps_The_Old_Title()
    {
        string id = await SeedAsync("keep me");
        var llm = new ScriptedLlmClient(new LlmEvent[0]);

        int exit = await RunAsync(id, llm);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That((await _store.GetAsync(id)).Value.Title).IsEqualTo("keep me");
        await Assert.That(_err.ToString()).Contains("usable title");
    }

    [Test]
    public async Task Missing_Session_Is_Store_Failure()
    {
        var llm = new ScriptedLlmClient(new LlmEvent[0]);

        int exit = await RunAsync("no-such-session", llm);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("Cannot regenerate title");
    }

    [Test]
    public async Task Empty_Id_Is_Argument_Error()
    {
        var llm = new ScriptedLlmClient(new LlmEvent[0]);

        int exit = await RunAsync("   ", llm);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(_err.ToString()).Contains("Usage: harbor sessions retitle");
    }

    [Test]
    public async Task Sanitize_Trims_And_Caps_The_Title()
    {
        string longTitle = new('x', 120);

        string? title = SessionTitleRegenerator.Sanitize($"   {longTitle}   ");

        await Assert.That(title).IsNotNull();
        await Assert.That(title!.Length).IsEqualTo(SessionTitleRegenerator.MaxTitleChars);
        await Assert.That(SessionTitleRegenerator.Sanitize("   ")).IsNull();
    }
}
