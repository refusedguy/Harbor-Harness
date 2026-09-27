using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Issue #199 acceptance: corrupt/empty session files surface as
///     <c>Result</c> failures naming the session, the reason, and the path —
///     never throws, never nulls. Missing sessions keep the exact
///     <c>Session '{id}' not found.</c> shape shared by all three stores.
/// </summary>
public class JsonlSessionStoreRopTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-rop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    [Test]
    public async Task GetAsync_CorruptHeader_ReturnsFailureNamingSessionAndReason()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            File.WriteAllText(path, "this is not a session header\n");

            var result = await store.GetAsync(session.Id);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains(session.Id);
            await Assert.That(result.Error).Contains("corrupt");
            await Assert.That(result.Error).Contains(path);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task GetAsync_EmptyFile_ReturnsFailureNamingSession()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            File.WriteAllText(path, string.Empty);

            var result = await store.GetAsync(session.Id);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains(session.Id);
            await Assert.That(result.Error).Contains("empty");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task GetAsync_MissingSession_ReturnsSharedNotFoundShape()
    {
        var store = CreateStore();
        try
        {
            var result = await store.GetAsync("nonexistent-id");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).IsEqualTo("Session 'nonexistent-id' not found.");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task GetMessagesAsync_CorruptLines_SkipsThemAndSucceeds()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "m1", session.Id, DateTimeOffset.UtcNow, "kept", "code", "claude-opus-4"));
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            File.AppendAllText(path, "{broken json line\n");

            var messages = await store.GetMessagesAsync(session.Id);

            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("kept");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task UpdateMessageAsync_UnknownMessage_FailureNamesMessageAndSession()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            var ghost = new UserMessage("msg-ghost", session.Id, DateTimeOffset.UtcNow, "Ghost", "code", "claude-opus-4");

            var result = await store.UpdateMessageAsync(session.Id, ghost);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).IsEqualTo(
                $"Message 'msg-ghost' not found in session '{session.Id}'.");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
