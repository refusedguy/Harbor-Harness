using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Storage.Tests;

/// <summary>
/// Tests for MemorySessionStore covering full CRUD: Create, Get, List, Append, GetMessages, Delete.
/// MemorySessionStore is in-process and ephemeral — perfect for unit testing.
/// </summary>
public class MemorySessionStoreTests
{
    private static MemorySessionStore Create() => new();

    private static UserMessage NewUserMessage(string sessionId, string content, string idSuffix = "")
        => new(
            Id: $"umsg-{idSuffix}{Guid.NewGuid():N}",
            SessionId: sessionId,
            CreatedAt: DateTimeOffset.UtcNow,
            Content: content,
            Agent: "code",
            Model: "claude-opus-4");

    [Test]
    public async Task CreateAsync_ReturnsSessionWithValidId()
    {
        var store = Create();
        var result = await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(string.IsNullOrEmpty(result.Value.Id)).IsFalse();
        await Assert.That(result.Value.Directory).IsEqualTo("/proj");
        await Assert.That(result.Value.Agent).IsEqualTo("code");
        await Assert.That(result.Value.ProviderId).IsEqualTo("anthropic");
        await Assert.That(result.Value.Model).IsEqualTo("claude-opus-4");
    }

    [Test]
    public async Task GetAsync_ReturnsCreatedSession()
    {
        var store = Create();
        var created = await store.CreateAsync("/proj", "code", "openai", "gpt-4o");

        var fetched = await store.GetAsync(created.Value.Id);

        await Assert.That(fetched.IsSuccess).IsTrue();
        await Assert.That(fetched.Value.Id).IsEqualTo(created.Value.Id);
        await Assert.That(fetched.Value.ProviderId).IsEqualTo("openai");
    }

    [Test]
    public async Task GetAsync_UnknownId_ReturnsFailure()
    {
        var store = Create();
        var result = await store.GetAsync("nonexistent-id");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ListAsync_ReturnsAllCreatedSessions()
    {
        var store = Create();
        await store.CreateAsync("/proj1", "code", "anthropic", "claude-opus-4");
        await store.CreateAsync("/proj2", "plan", "openai", "gpt-4o");
        await store.CreateAsync("/proj3", "explore", "ollama", "llama3.2");

        var list = await store.ListAsync();

        await Assert.That(list.IsSuccess).IsTrue();
        await Assert.That(list.Value.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ListAsync_FiltersByProjectId()
    {
        var store = Create();
        var s1 = await store.CreateAsync("/projA", "code", "anthropic", "claude-opus-4");
        await store.CreateAsync("/projB", "code", "openai", "gpt-4o");

        var list = await store.ListAsync(s1.Value.ProjectId);

        await Assert.That(list.IsSuccess).IsTrue();
        await Assert.That(list.Value.Count).IsEqualTo(1);
        await Assert.That(list.Value[0].ProjectId).IsEqualTo(s1.Value.ProjectId);
    }

    [Test]
    public async Task AppendMessageAsync_PersistsMessage()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        var msg = NewUserMessage(session.Id, "hello", "1");

        var appendResult = await store.AppendMessageAsync(session.Id, msg);
        await Assert.That(appendResult.IsSuccess).IsTrue();

        var messages = await store.GetMessagesAsync(session.Id);
        await Assert.That(messages.IsSuccess).IsTrue();
        await Assert.That(messages.Value.Count).IsEqualTo(1);
        await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("hello");
    }

    [Test]
    public async Task AppendMessageAsync_UnknownSession_ReturnsFailure()
    {
        var store = Create();
        var msg = NewUserMessage("nonexistent", "hello");
        var result = await store.AppendMessageAsync("nonexistent", msg);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task GetMessagesAsync_ReturnsInCreatedAtOrder()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        var baseTime = DateTimeOffset.UtcNow;

        await store.AppendMessageAsync(session.Id, new UserMessage(
            Id: "m1", SessionId: session.Id, CreatedAt: baseTime.AddSeconds(1), Content: "first", Agent: "code", Model: "claude"));
        await store.AppendMessageAsync(session.Id, new UserMessage(
            Id: "m2", SessionId: session.Id, CreatedAt: baseTime.AddSeconds(3), Content: "third", Agent: "code", Model: "claude"));
        await store.AppendMessageAsync(session.Id, new UserMessage(
            Id: "m3", SessionId: session.Id, CreatedAt: baseTime.AddSeconds(2), Content: "second", Agent: "code", Model: "claude"));

        var messages = await store.GetMessagesAsync(session.Id);

        await Assert.That(messages.Value.Count).IsEqualTo(3);
        await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("first");
        await Assert.That(((UserMessage)messages.Value[1]).Content).IsEqualTo("second");
        await Assert.That(((UserMessage)messages.Value[2]).Content).IsEqualTo("third");
    }

    [Test]
    public async Task GetMessagesAsync_UnknownSession_ReturnsFailure()
    {
        var store = Create();
        var result = await store.GetMessagesAsync("nonexistent");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task DeleteAsync_RemovesSessionAndMessages()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        await store.AppendMessageAsync(session.Id, NewUserMessage(session.Id, "hello"));

        var deleteResult = await store.DeleteAsync(session.Id);
        await Assert.That(deleteResult.IsSuccess).IsTrue();

        var fetched = await store.GetAsync(session.Id);
        await Assert.That(fetched.IsFailure).IsTrue();

        var messages = await store.GetMessagesAsync(session.Id);
        await Assert.That(messages.IsFailure).IsTrue();
    }

    [Test]
    public async Task DeleteAsync_UnknownId_StillSucceeds()
    {
        var store = Create();
        var result = await store.DeleteAsync("nonexistent");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Clear_RemovesAllSessions()
    {
        var store = Create();
        await store.CreateAsync("/proj1", "code", "anthropic", "claude-opus-4");
        await store.CreateAsync("/proj2", "code", "openai", "gpt-4o");
        await store.CreateAsync("/proj3", "code", "ollama", "llama3.2");

        store.Clear();

        var list = await store.ListAsync();
        await Assert.That(list.IsSuccess).IsTrue();
        await Assert.That(list.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateMessageAsync_ReplacesExistingMessage()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        var original = NewUserMessage(session.Id, "original", "1");
        await store.AppendMessageAsync(session.Id, original);

        var updated = original with { Content = "edited" };
        await store.UpdateMessageAsync(session.Id, updated);

        var messages = await store.GetMessagesAsync(session.Id);
        await Assert.That(messages.Value.Count).IsEqualTo(1);
        await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("edited");
    }

    [Test]
    public async Task GetStatsAsync_ReturnsSessionMetadata()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        var stats = await store.GetStatsAsync(session.Id);

        await Assert.That(stats.IsSuccess).IsTrue();
        await Assert.That(stats.Value.MessageCount).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateStatsAsync_PersistsMetadata()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

        var newMeta = new SessionMetadata(Cost: 0.42m, TokensInput: 100, TokensOutput: 50, TokensReasoning: 0, TokensCacheRead: 0, TokensCacheWrite: 0, MessageCount: 2, TimeCompacting: null);
        var updateResult = await store.UpdateStatsAsync(session.Id, newMeta);
        await Assert.That(updateResult.IsSuccess).IsTrue();

        var stats = await store.GetStatsAsync(session.Id);
        await Assert.That(stats.Value.Cost).IsEqualTo(0.42m);
        await Assert.That(stats.Value.TokensInput).IsEqualTo(100);
        await Assert.That(stats.Value.MessageCount).IsEqualTo(2);
    }
}
