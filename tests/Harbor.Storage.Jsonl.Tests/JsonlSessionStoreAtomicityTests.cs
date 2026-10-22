using System.Text.Json;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Issue #83 regression tests: unsanitized session ids must never escape
///     the store root, and full-file rewrites must be atomic (temp + rename —
///     a crash leaves the old file or the new file, never a torn one).
/// </summary>
public class JsonlSessionStoreAtomicityTests
{
    private static readonly string[] TraversalIds =
    [
        "../evil",
        "..\\evil",
        "..",
        "a/b",
        "/abs",
        "",
        "has space",
        "x.jsonl",
        "a:b",
    ];

    private static (JsonlSessionStore Store, string Root) CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-83-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return (new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance), tempDir);
    }

    [Test]
    public async Task Traversal_Ids_Rejected_By_All_Methods_And_Never_Escape_Root()
    {
        var (store, root) = CreateStore();
        try
        {
            string? parentMarker = null;
            foreach (string evil in TraversalIds)
            {
                var get = await store.GetAsync(evil);
                await Assert.That(get).IsFailed();

                var list = await store.GetMessagesAsync(evil);
                await Assert.That(list).IsFailed();

                var stats = await store.GetStatsAsync(evil);
                await Assert.That(stats).IsFailed();

                var append = await store.AppendMessageAsync(evil,
                    TestMessages.User("hi", id: "m1", sessionId: evil, model: "claude"));
                await Assert.That(append).IsFailed();

                var updateMsg = await store.UpdateMessageAsync(evil,
                    TestMessages.User("hi", id: "m1", sessionId: evil, model: "claude"));
                await Assert.That(updateMsg).IsFailed();

                var delete = await store.DeleteAsync(evil);
                await Assert.That(delete).IsFailed();

                var truncate = await store.DeleteMessagesAfterAsync(evil, "m1");
                await Assert.That(truncate).IsFailed();

                var seed = Harbor.Abstractions.Models.Session.Create("/t", "code", "anthropic", "m");
                var renamed = seed with { Id = evil };
                var update = await store.UpdateAsync(renamed);
                await Assert.That(update).IsFailed();

                // Nothing may materialize outside (or inside) the root for these ids.
                parentMarker = Path.Combine(Path.GetDirectoryName(root)!, "evil");
                await Assert.That(File.Exists(parentMarker)).IsFalse();
            }

            await Assert.That(Directory.GetFiles(root).Length).IsEqualTo(0);
            await Assert.That(parentMarker is null || !File.Exists(parentMarker)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Rewrites_Leave_Valid_File_And_No_Temp_Leftovers()
    {
        var (store, root) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;

            await store.AppendMessageAsync(session.Id, TestMessages.User("first", id: "msg-1", sessionId: session.Id, createdAt: DateTimeOffset.UtcNow.AddSeconds(-2), model: "claude"));
            await store.AppendMessageAsync(session.Id, TestMessages.User("second", id: "msg-2", sessionId: session.Id, createdAt: DateTimeOffset.UtcNow.AddSeconds(-1), model: "claude"));

            // All three atomic-rewrite paths (#83.1). The edit comes FIRST:
            // a message keeps its place in the conversation (#1798 — the place
            // it holds is where it was written FIRST), so a rewind anchored on
            // it still removes the messages that follow it. The rewind itself
            // is an event now (#1752), so it appends instead of rewriting.
            var edit = await store.UpdateMessageAsync(session.Id, TestMessages.User("edited", id: "msg-1", sessionId: session.Id, createdAt: DateTimeOffset.UtcNow.AddSeconds(-2), model: "claude"));
            await Assert.That(edit).IsSuccessful();

            var truncate = await store.DeleteMessagesAfterAsync(session.Id, "msg-1");
            await Assert.That(truncate).IsSuccessful();
            await Assert.That(truncate.Value).IsEqualTo(1);

            var renamed = session with { Title = "Renamed" };
            var update = await store.UpdateAsync(renamed);
            await Assert.That(update).IsSuccessful();

            // No temp files linger: temp + rename must clean up after itself.
            await Assert.That(Directory.GetFiles(root, "*.tmp").Length).IsEqualTo(0);
            string[] files = Directory.GetFiles(root, "*.jsonl");
            await Assert.That(files.Length).IsEqualTo(1);

            // The surviving file is whole: every line parses, header is first.
            string[] lines = await File.ReadAllLinesAsync(files[0]);
            await Assert.That(lines.Length).IsGreaterThan(0);
            await Assert.That(lines[0].Contains("\"type\":\"session\"", StringComparison.Ordinal)).IsTrue();
            foreach (string line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
            }

            // And the store reads it back coherently.
            var reread = await store.GetAsync(session.Id);
            await Assert.That(reread).IsSuccessful();
            await Assert.That(reread.Value.Title).IsEqualTo("Renamed");

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages).IsSuccessful();
            await Assert.That(messages.Value.Count).IsEqualTo(1);
            await Assert.That(((UserMessage)messages.Value[0]).Content).IsEqualTo("edited");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Concurrent_Rewrites_Never_Tear_The_File()
    {
        var (store, root) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, TestMessages.User("hello", id: "msg-1", sessionId: session.Id, model: "claude"));

            // Hammer the rewrite path from many tasks; every observable state
            // of the file must be either the old or a new complete version.
            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                int index = i;
                tasks[i] = Task.Run(async () =>
                {
                    var candidate = session with { Title = $"Title-{index}" };
                    await store.UpdateAsync(candidate);
                });
            }

            await Task.WhenAll(tasks);

            string file = Path.Combine(root, $"{session.Id}.jsonl");
            string[] lines = await File.ReadAllLinesAsync(file);
            foreach (string line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
            }

            await Assert.That(Directory.GetFiles(root, "*.tmp").Length).IsEqualTo(0);

            var reread = await store.GetAsync(session.Id);
            await Assert.That(reread).IsSuccessful();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
