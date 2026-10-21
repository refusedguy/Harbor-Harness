using System.Reflection;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;
namespace Harbor.Storage.Jsonl.Tests;
public class JsonlSessionStoreTests
{
    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    [Test]
    public async Task CreateAsync_ReturnsValidSession()
    {
        var store = CreateStore();
        try
        {
            var result = await store.CreateAsync("/test/dir", "code", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/test/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
            await Assert.That(result.Value.ProviderId).IsEqualTo("anthropic");
            await Assert.That(result.Value.Model).IsEqualTo("claude-opus-4");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task AppendMessageAsync_PersistsMessage()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;

            var userMsg = TestMessages.User("Hello", id: "msg-1", sessionId: session.Id, model: "claude-opus-4");

            var appendResult = await store.AppendMessageAsync(session.Id, userMsg);
            await Assert.That(appendResult.IsSuccess).IsTrue();

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("Hello");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task GetMessagesAsync_ReturnsInOrder()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;

            await store.AppendMessageAsync(session.Id, TestMessages.User("first", id: "m1", sessionId: session.Id, createdAt: DateTimeOffset.UtcNow.AddSeconds(-2), model: "claude"));
            await store.AppendMessageAsync(session.Id, TestMessages.User("second", id: "m2", sessionId: session.Id, createdAt: DateTimeOffset.UtcNow.AddSeconds(-1), model: "claude"));
            await store.AppendMessageAsync(session.Id, TestMessages.User("third", id: "m3", sessionId: session.Id, model: "claude"));

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.Value.Count).IsEqualTo(3);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("first");
            await Assert.That(((UserMessage)messages.Value[1]).Content).IsEqualTo("second");
            await Assert.That(((UserMessage)messages.Value[2]).Content).IsEqualTo("third");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task ListAsync_ReturnsAllSessions()
    {
        var store = CreateStore();
        try
        {
            await store.CreateAsync("/test1", "code", "anthropic", "claude-opus-4");
            await store.CreateAsync("/test2", "code", "openai", "gpt-4o");

            var list = await store.ListAsync();
            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Count).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task ListAsync_AfterMessages_ReturnsHeaderOnlyWithEmptyMetadata()
    {
        // #1101 N+1: List must not reparse history per session. After messages
        // land, Get derives stats from them while List stays header-only.
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, TestMessages.User("hello", id: "m1", sessionId: session.Id, model: "claude-opus-4"));
            await store.AppendMessageAsync(session.Id, TestMessages.Assistant("hi", id: "m2", sessionId: session.Id, usage: new Usage(10, 5), model: "claude-opus-4"));

            var list = await store.ListAsync();
            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Count).IsEqualTo(1);
            await Assert.That(list.Value[0].Id).IsEqualTo(session.Id);
            await Assert.That(list.Value[0].Metadata).IsEqualTo(SessionMetadata.Empty);

            var got = await store.GetAsync(session.Id);
            await Assert.That(got.IsSuccess).IsTrue();
            await Assert.That(got.Value.Metadata.MessageCount).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task DeleteAsync_RemovesSession()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;

            var deleteResult = await store.DeleteAsync(session.Id);
            await Assert.That(deleteResult.IsSuccess).IsTrue();

            var getResult = await store.GetAsync(session.Id);
            await Assert.That(getResult.IsSuccess).IsFalse();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task DeleteAsync_UnknownId_ReturnsFailure()
    {
        var store = CreateStore();
        try
        {
            var result = await store.DeleteAsync("nonexistent");
            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task UpdateMessageAsync_UnknownSession_ReturnsFailure()
    {
        var store = CreateStore();
        try
        {
            var msg = TestMessages.User("Hello", id: "msg-1", sessionId: "nonexistent", model: "claude-opus-4");
            var result = await store.UpdateMessageAsync("nonexistent", msg);
            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task UpdateMessageAsync_UnknownMessage_ReturnsFailure()
    {
        var store = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            var ghost = TestMessages.User("Ghost", id: "msg-ghost", sessionId: session.Id, model: "claude-opus-4");
            var result = await store.UpdateMessageAsync(session.Id, ghost);
            await Assert.That(result.IsFailure).IsTrue();

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}

internal static class JsonlSessionStoreExtensions
{
    public static string GetRootDirectory(this JsonlSessionStore store)
    {
        var field = typeof(JsonlSessionStore).GetField("_rootDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)field!.GetValue(store)!;
    }
}

/// <summary>
///     ROP-B П.11: cancellation is NOT a store failure — CreateAsync must let
///     <see cref="OperationCanceledException" /> propagate instead of masking
///     an Esc press as "Operation was cancelled." session error.
/// </summary>
public class JsonlSessionStoreCancellationTests
{
    [Test]
    public async Task CreateAsync_PreCancelledToken_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.That(async () =>
                await store.CreateAsync("/dir", "code", "anthropic", "claude-opus-4", cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task CreateAsync_CancelledMidway_DoesNotReturnFailureResult()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            // Either completes fast enough or propagates — never a Failure result.
            try
            {
                var result = await store.CreateAsync("/dir", "code", "anthropic", "claude-opus-4", cts.Token);
                await Assert.That(result.IsSuccess).IsTrue();
            }
            catch (OperationCanceledException)
            {
                // expected under throttled CI
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task AppendMessageAsync_PreCancelledToken_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var message = TestMessages.User("hi", id: "m1", sessionId: "s1", model: "claude-opus-4");

            await Assert.That(async () =>
                await store.AppendMessageAsync("s1", message, cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task DeleteAsync_PreCancelledToken_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.That(async () =>
                await store.DeleteAsync("s1", cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task UpdateAsync_PreCancelledToken_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4");
            await Assert.That(created.IsSuccess).IsTrue();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.That(async () =>
                await store.UpdateAsync(created.Value, cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetMessagesAsync_PreCancelledToken_ExistingFile_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4");
            await Assert.That(created.IsSuccess).IsTrue();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Cache miss + existing file → ReadLineAsync(ct) observes cancellation.
            await Assert.That(async () =>
                await store.GetMessagesAsync(created.Value.Id, cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetAsync_PreCancelledToken_ExistingFile_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4");
            await Assert.That(created.IsSuccess).IsTrue();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Existing header file → ReadHeaderAsync's ReadLineAsync(ct) observes cancellation
            // instead of surfacing as a red Failure result (ROP-B П.11 residual).
            await Assert.That(async () =>
                await store.GetAsync(created.Value.Id, cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ListAsync_PreCancelledToken_ExistingFile_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4");
            await Assert.That(created.IsSuccess).IsTrue();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Enumeration reaches GetAsync per file; the cancellation rides out.
            await Assert.That(async () =>
                await store.ListAsync(null, cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetStatsAsync_PreCancelledToken_ExistingFile_PropagatesCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4");
            await Assert.That(created.IsSuccess).IsTrue();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Stats re-read the message log from disk → ReadLineAsync(ct) observes cancellation.
            await Assert.That(async () =>
                await store.GetStatsAsync(created.Value.Id, cts.Token)
            ).Throws<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetAsync_LegacyHeaderWithoutUpdatedAt_ReturnsStableTimestampAcrossReads()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            // DDD-audit 25.08 (ROP-C Z3): legacy fixture written before the
            // header carried "updatedAt". GetAsync used to fabricate UtcNow on
            // every read, so two consecutive reads disagreed and ListAsync's
            // recency sort was random.
            string sessionId = "legacy01";
            string sessionFile = Path.Combine(tempDir, $"{sessionId}.jsonl");
            DateTimeOffset created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            await File.WriteAllTextAsync(sessionFile,
                $$"""
                {"type":"session","version":1,"id":"{{sessionId}}","projectId":"p","directory":"/tmp/x","title":"Legacy","agent":"code","model":"m","providerId":"anthropic","createdAt":"{{created:O}}"}
                """);

            var first = await store.GetAsync(sessionId);
            var second = await store.GetAsync(sessionId);

            await Assert.That(first.IsSuccess).IsTrue();
            await Assert.That(second.IsSuccess).IsTrue();
            await Assert.That(first.Value.CreatedAt).IsEqualTo(created);
            // The fabricated-UtcNow defect made this assertion flaky by design:
            // the timestamp must be identical across consecutive reads.
            await Assert.That(second.Value.UpdatedAt).IsEqualTo(first.Value.UpdatedAt);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task UpdateAsync_RefreshesStoredUpdatedAt()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = (await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4")).Value;
            var renamed = created with { Title = "Renamed" };

            var updated = await store.UpdateAsync(renamed);
            var reread = await store.GetAsync(created.Id);

            await Assert.That(updated.IsSuccess).IsTrue();
            await Assert.That(reread.IsSuccess).IsTrue();
            await Assert.That(reread.Value.Title).IsEqualTo("Renamed");
            await Assert.That(reread.Value.UpdatedAt).IsGreaterThanOrEqualTo(renamed.CreatedAt);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task UpdateMessageAsync_AppendsANewVersion_TheOldOneStaysInTheLog()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
        try
        {
            var created = (await store.CreateAsync(tempDir, "code", "anthropic", "claude-opus-4")).Value;
            string sessionId = created.Id;

            // #1752: an edit appends a NEW VERSION of the record. The version
            // it replaces stays in the log — that is what "the agent's text
            // never disappears" means — and the surface shows the new one.
            var original = TestMessages.User("before", id: "msg-1", sessionId: sessionId, agent: "user", model: "claude-opus-4");
            var appendResult = await store.AppendMessageAsync(sessionId, original);
            await Assert.That(appendResult.IsSuccess).IsTrue();

            var editResult = await store.UpdateMessageAsync(sessionId, TestMessages.User("after", id: "msg-1", sessionId: sessionId, createdAt: original.CreatedAt, agent: "user", model: "claude-opus-4"));
            await Assert.That(editResult.IsSuccess).IsTrue();

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(tempDir, $"{sessionId}.jsonl"));
            int occurrences = lines.Count(l => l.Contains("\"msg-1\"", StringComparison.Ordinal));
            await Assert.That(occurrences).IsEqualTo(2);

            var messages = (await store.GetMessagesAsync(sessionId)).Value;
            await Assert.That(messages.Count).IsEqualTo(1);
            var user = (UserMessage)messages[0];
            await Assert.That(user.Content).IsEqualTo("after");

            // Header survived the append.
            await Assert.That(lines[0].Contains("\"type\":\"session\"", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
