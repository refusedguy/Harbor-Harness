using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

public class SessionForkRunnerTests
{
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

    private static AssistantMessage Assistant(string content) => new(
        Guid.NewGuid().ToString("N"),
        "s",
        DateTimeOffset.UtcNow,
        [new TextPart(content)],
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    [Test]
    public async Task Fork_CopiesPrefixInclusive_KeepsSourceIntact()
    {
        var source = await SeedAsync(_store, "lineage root", User("q1"), Assistant("a1"), User("q2"), Assistant("a2"));
        var history = (await _store.GetMessagesAsync(source.Id)).Value;
        string cutPoint = history[1].Id.ToString(); // inclusive cut after a1

        var forked = await new SessionForkRunner(_store).ForkAsync(source.Id, cutPoint);

        await Assert.That(forked.IsSuccess).IsTrue();
        await Assert.That(forked.Value.Copied).IsEqualTo(2);

        // Source untouched.
        await Assert.That((await _store.GetMessagesAsync(source.Id)).Value.Count).IsEqualTo(4);

        // Fork carries prefix [q1, a1] in order, correct lineage and title suffix.
        var forkHeader = (await _store.GetAsync(forked.Value.ForkId)).Value;
        await Assert.That(forkHeader.ParentSessionId).IsEqualTo(source.Id);
        await Assert.That(forkHeader.Title).Contains("(fork)");
        await Assert.That(forkHeader.Agent).IsEqualTo("code");
        await Assert.That(forkHeader.ProviderId).IsEqualTo("test");
        await Assert.That(forkHeader.Model).IsEqualTo("test-model");

        var forkMessages = (await _store.GetMessagesAsync(forked.Value.ForkId)).Value;
        await Assert.That(forkMessages.Count).IsEqualTo(2);
        await Assert.That(forkMessages[0].Id.ToString()).IsEqualTo(history[0].Id.ToString());
        await Assert.That(forkMessages[1].Id.ToString()).IsEqualTo(history[1].Id.ToString());
    }

    [Test]
    public async Task Fork_LastMessage_CopiesEverything()
    {
        var source = await SeedAsync(_store, "", User("only question"), Assistant("only answer"));
        var last = (await _store.GetMessagesAsync(source.Id)).Value[^1].Id.ToString();

        var forked = await new SessionForkRunner(_store).ForkAsync(source.Id, last);

        await Assert.That(forked.IsSuccess).IsTrue();
        await Assert.That(forked.Value.Copied).IsEqualTo(2);
        await Assert.That((await _store.GetMessagesAsync(forked.Value.ForkId)).Value.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Fork_UnknownMessageId_FailsWithoutCreatingFork()
    {
        var source = await SeedAsync(_store, "", User("q"));
        int sessionsBefore = (await _store.ListAsync()).Value.Count;

        var forked = await new SessionForkRunner(_store).ForkAsync(source.Id, "no-such-message-id");

        await Assert.That(forked.IsFailure).IsTrue();
        await Assert.That((await _store.ListAsync()).Value.Count).IsEqualTo(sessionsBefore); // nothing created
        await Assert.That(forked.Error).Contains("not found");
    }

    [Test]
    public async Task Fork_UnknownSession_Fails()
    {
        var forked = await new SessionForkRunner(_store).ForkAsync("missing-session", Guid.NewGuid().ToString("N"));

        await Assert.That(forked.IsFailure).IsTrue();
        await Assert.That(forked.Error).Contains("Cannot load session");
    }
}
