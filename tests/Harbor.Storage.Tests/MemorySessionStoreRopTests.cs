using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;

namespace Harbor.Storage.Tests;

/// <summary>
///     Issue #199 acceptance: the memory store honors the shared
///     <c>Result</c> failure shapes — every missing-session outcome names the
///     session with the exact <c>Session '{id}' not found.</c> text, and an
///     empty history is success (not failure).
/// </summary>
public class MemorySessionStoreRopTests
{
    private static MemorySessionStore Create() => new();

    private static UserMessage NewUserMessage(string sessionId, string content)
        => new(
            $"umsg-{Guid.NewGuid():N}",
            sessionId,
            DateTimeOffset.UtcNow,
            content,
            "code",
            "claude-opus-4");

    [Test]
    public async Task MissingSession_FailuresCarrySharedNotFoundShape()
    {
        var store = Create();
        const string missing = "nonexistent-id";

        await Assert.That((await store.GetAsync(missing)).Error)
            .IsEqualTo("Session 'nonexistent-id' not found.");
        await Assert.That((await store.GetMessagesAsync(missing)).Error)
            .IsEqualTo("Session 'nonexistent-id' not found.");
        await Assert.That((await store.DeleteAsync(missing)).Error)
            .IsEqualTo("Session 'nonexistent-id' not found.");
        await Assert.That((await store.AppendMessageAsync(missing, NewUserMessage(missing, "hi"))).Error)
            .IsEqualTo("Session 'nonexistent-id' not found.");
    }

    [Test]
    public async Task GetMessagesAsync_FreshSession_ReturnsEmptySuccess()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;

        var messages = await store.GetMessagesAsync(session.Id);

        await Assert.That(messages.IsSuccess).IsTrue();
        await Assert.That(messages.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateMessageAsync_UnknownMessage_FailureNamesMessageAndSession()
    {
        var store = Create();
        var session = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        var ghost = NewUserMessage(session.Id, "ghost");

        var result = await store.UpdateMessageAsync(session.Id, ghost);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(ghost.Id);
        await Assert.That(result.Error).Contains(session.Id);
    }
}
