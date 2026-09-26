using System.Text.Json;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

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
                await Assert.That(get.IsFailure).IsTrue();

                var list = await store.GetMessagesAsync(evil);
                await Assert.That(list.IsFailure).IsTrue();

                var stats = await store.GetStatsAsync(evil);
                await Assert.That(stats.IsFailure).IsTrue();

                var append = await store.AppendMessageAsync(evil,
                    new UserMessage("m1", evil, DateTimeOffset.UtcNow, "hi", "code", "claude"));
                await Assert.That(append.IsFailure).IsTrue();

                var updateMsg = await store.UpdateMessageAsync(evil,
                    new UserMessage("m1", evil, DateTimeOffset.UtcNow, "hi", "code", "claude"));
                await Assert.That(updateMsg.IsFailure).IsTrue();

                var delete = await store.DeleteAsync(evil);
                await Assert.That(delete.IsFailure).IsTrue();

                var truncate = await store.DeleteMessagesAfterAsync(evil, "m1");
                await Assert.That(truncate.IsFailure).IsTrue();

                var seed = Harbor.Abstractions.Models.Session.Create("/t", "code", "anthropic", "m");
                var renamed = seed with { Id = evil };
                var update = await store.UpdateAsync(renamed);
                await Assert.That(update.IsFailure).IsTrue();

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

            await store.AppendMessageAsync(session.Id, new UserMessage(
                "msg-1", session.Id, DateTimeOffset.UtcNow.AddSeconds(-2), "first", "code", "claude"));
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "msg-2", session.Id, DateTimeOffset.UtcNow.AddSeconds(-1), "second", "code", "claude"));

            // All three atomic-rewrite paths (#83.1).
            var edit = await store.UpdateMessageAsync(session.Id, new UserMessage(
                "msg-1", session.Id, DateTimeOffset.UtcNow.AddSeconds(-2), "edited", "code", "claude"));
            await Assert.That(edit.IsSuccess).IsTrue();

            var renamed = session with { Title = "Renamed" };
            var update = await store.UpdateAsync(renamed);
            await Assert.That(update.IsSuccess).IsTrue();

            var truncate = await store.DeleteMessagesAfterAsync(session.Id, "msg-1");
            await Assert.That(truncate.IsSuccess).IsTrue();
            await Assert.That(truncate.Value).IsEqualTo(1);

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
            await Assert.That(reread.IsSuccess).IsTrue();
            await Assert.That(reread.Value.Title).IsEqualTo("Renamed");

            var messages = await store.GetMessagesAsync(session.Id);
            await Assert.That(messages.IsSuccess).IsTrue();
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
            await store.AppendMessageAsync(session.Id, new UserMessage(
                "msg-1", session.Id, DateTimeOffset.UtcNow, "hello", "code", "claude"));

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
            await Assert.That(reread.IsSuccess).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
