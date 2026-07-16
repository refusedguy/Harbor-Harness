using System.Reflection;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
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

            var userMsg = new UserMessage(
                "msg-1",
                session.Id,
                DateTimeOffset.UtcNow,
                "Hello",
                "code",
                "claude-opus-4");

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

            await store.AppendMessageAsync(session.Id, new UserMessage("m1", session.Id, DateTimeOffset.UtcNow.AddSeconds(-2), "first", "code", "claude"));
            await store.AppendMessageAsync(session.Id, new UserMessage("m2", session.Id, DateTimeOffset.UtcNow.AddSeconds(-1), "second", "code", "claude"));
            await store.AppendMessageAsync(session.Id, new UserMessage("m3", session.Id, DateTimeOffset.UtcNow, "third", "code", "claude"));

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
}

internal static class JsonlSessionStoreExtensions
{
    public static string GetRootDirectory(this JsonlSessionStore store)
    {
        var field = typeof(JsonlSessionStore).GetField("_rootDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)field!.GetValue(store)!;
    }
}
