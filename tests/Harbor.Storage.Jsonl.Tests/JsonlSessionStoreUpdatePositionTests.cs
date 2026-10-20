using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     <b>#1798:</b> an update keeps its record at its position in the file.
///     The file order IS the insertion order — the rewind
///     (<c>DeleteMessagesAfterAsync</c>) anchors on a record's position in the
///     file — so an edit that relocates its record to the tail of the file
///     moves the message in the history without a trace, and a rewind anchored
///     on it removes nothing (the anchor is the last record, so nothing
///     follows it).
/// </summary>
/// <remarks>
///     The guard is deliberately about the FILE, not about the read order: the
///     reader sorts by <c>CreatedAt</c> when the file is not append-linear in
///     time, so a moved record often still reads back in the right order — the
///     damage is in the file, where the rewind and the log fold look.
/// </remarks>
public class JsonlSessionStoreUpdatePositionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonlSessionStore CreateStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"harbor-test-1798-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
    }

    /// <summary>
    ///     A session with four messages: the header plus m0..m3, in that order.
    /// </summary>
    private static async Task<(JsonlSessionStore Store, string SessionId, string FilePath)> SeedAsync()
    {
        var store = CreateStore(out string root);
        var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
        for (int i = 0; i < 4; i++)
        {
            var append = await store.AppendMessageAsync(
                session.Id,
                TestMessages.User($"body {i}", id: $"m{i}", sessionId: session.Id, createdAt: Epoch.AddSeconds(i), model: "claude-opus-4"));
            await Assert.That(append.IsSuccess).IsTrue();
        }

        return (store, session.Id, Path.Combine(root, $"{session.Id}.jsonl"));
    }

    /// <summary>
    ///     The line each message record sits on, keyed by the message id — the
    ///     map the invariant is about. The FIRST record of an id wins, the
    ///     same way the reader resolves a repeated id.
    /// </summary>
    private static Dictionary<string, int> MessageLineIndexes(string[] lines)
    {
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type)
                && type.GetString() == "message"
                && root.TryGetProperty("id", out var id)
                && id.GetString() is { } messageId
                && !indexes.ContainsKey(messageId))
            {
                indexes[messageId] = i;
            }
        }

        return indexes;
    }

    /// <summary>
    ///     The id of the message record on the given line — empty when the line
    ///     carries none, so the assertion that uses it fails on the value
    ///     rather than on a null.
    /// </summary>
    private static string MessageIdOn(string[] lines, int line)
    {
        using var doc = JsonDocument.Parse(lines[line]);
        var root = doc.RootElement;
        return root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
    }

    [Test]
    public async Task UpdateMessage_KeepsTheRecordAtItsPositionInTheFile()
    {
        var (store, sessionId, path) = await SeedAsync();
        try
        {
            string[] before = await File.ReadAllLinesAsync(path);
            Dictionary<string, int> beforeIndexes = MessageLineIndexes(before);
            await Assert.That(beforeIndexes.Count).IsEqualTo(4);

            // The edit changes the body and nothing else — the same message id,
            // the same instant. This is what an edit IS: the same record, with
            // a different payload.
            var edited = await store.UpdateMessageAsync(sessionId, TestMessages.User("edited body", id: "m1", sessionId: sessionId, createdAt: Epoch.AddSeconds(1), model: "claude-opus-4"));
            await Assert.That(edited.IsSuccess).IsTrue();

            string[] after = await File.ReadAllLinesAsync(path);
            Dictionary<string, int> afterIndexes = MessageLineIndexes(after);

            // The record count is unchanged: an update REPLACES, it does not
            // append (and it does not drop the record it replaced).
            await Assert.That(after.Length).IsEqualTo(before.Length);
            await Assert.That(afterIndexes.Count).IsEqualTo(beforeIndexes.Count);

            // The edited record is on the line it was on...
            await Assert.That(afterIndexes["m1"]).IsEqualTo(beforeIndexes["m1"]);

            // ...and so is every other record: the edit moved nothing.
            await Assert.That(afterIndexes["m0"]).IsEqualTo(beforeIndexes["m0"]);
            await Assert.That(afterIndexes["m2"]).IsEqualTo(beforeIndexes["m2"]);
            await Assert.That(afterIndexes["m3"]).IsEqualTo(beforeIndexes["m3"]);

            // The record that stayed put is the edited one, with the new body.
            await Assert.That(MessageIdOn(after, afterIndexes["m1"])).IsEqualTo("m1");
            await Assert.That(after[afterIndexes["m1"]].Contains("edited body", StringComparison.Ordinal)).IsTrue();

            // And the history still reads in insertion order.
            var messages = await store.GetMessagesAsync(sessionId);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(4);
            await Assert.That(messages.Value[0].Id).IsEqualTo("m0");
            await Assert.That(messages.Value[1].Id).IsEqualTo("m1");
            await Assert.That(messages.Value[2].Id).IsEqualTo("m2");
            await Assert.That(messages.Value[3].Id).IsEqualTo("m3");
            await Assert.That(((UserMessage)messages.Value[1]).Content).IsEqualTo("edited body");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task UpdateMessage_ThenRewindToIt_DropsTheMessagesThatFollowIt()
    {
        // The consequence of the position, and the reason the position is
        // worth an invariant: the rewind anchors on a record's place in the
        // FILE. A record that an edit moved to the tail is the last record, so
        // a rewind anchored on it drops nothing — the tail of the history
        // survives a rewind that was asked to remove it.
        var (store, sessionId, path) = await SeedAsync();
        try
        {
            var edited = await store.UpdateMessageAsync(sessionId, TestMessages.User("edited body", id: "m1", sessionId: sessionId, createdAt: Epoch.AddSeconds(1), model: "claude-opus-4"));
            await Assert.That(edited.IsSuccess).IsTrue();

            var rewind = await store.DeleteMessagesAfterAsync(sessionId, "m1");
            await Assert.That(rewind.IsSuccess).IsTrue();

            // m2 and m3 follow m1 in the file, so they are what the rewind
            // removes.
            await Assert.That(rewind.Value).IsEqualTo(2);

            string[] after = await File.ReadAllLinesAsync(path);
            Dictionary<string, int> afterIndexes = MessageLineIndexes(after);
            await Assert.That(afterIndexes.Count).IsEqualTo(2);
            await Assert.That(MessageIdOn(after, afterIndexes["m0"])).IsEqualTo("m0");
            await Assert.That(MessageIdOn(after, afterIndexes["m1"])).IsEqualTo("m1");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
