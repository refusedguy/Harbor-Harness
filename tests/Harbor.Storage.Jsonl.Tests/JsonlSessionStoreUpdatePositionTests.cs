using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     <b>#1798, kept — in the form the append-only log gives it (#1752).</b>
///     An edit used to rewrite the record in place, and the record's place in
///     the FILE was the thing #1798 pinned: the rewind anchors on a record's
///     position in the file, so an edit that moved its record to the tail of
///     the file moved the message in the history without a trace.
///     <para>
///         The log is append-only now, so the edit writes a NEW VERSION at the
///         end of the file and the old record stays where it was. The place a
///         message holds is therefore the place it was written FIRST — the
///         same rule the fold and the reader apply — and these tests pin that
///         the edit does not move the message, and that the rewind still cuts
///         at the right place.
///     </para>
/// </summary>
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
    public async Task UpdateMessage_KeepsTheMessageInItsPlaceInTheHistory()
    {
        var (store, sessionId, path) = await SeedAsync();
        try
        {
            string[] before = await File.ReadAllLinesAsync(path);
            Dictionary<string, int> beforeIndexes = MessageLineIndexes(before);
            await Assert.That(beforeIndexes.Count).IsEqualTo(4);

            // The edit changes the body and nothing else — the same message id,
            // the same instant. This is what an edit IS: the same message, with
            // a different payload.
            var edited = await store.UpdateMessageAsync(sessionId, TestMessages.User("edited body", id: "m1", sessionId: sessionId, createdAt: Epoch.AddSeconds(1), model: "claude-opus-4"));
            await Assert.That(edited.IsSuccess).IsTrue();

            string[] after = await File.ReadAllLinesAsync(path);
            Dictionary<string, int> afterIndexes = MessageLineIndexes(after);

            // The old record is still there — that is the point of an
            // append-only log — and the new version was appended.
            await Assert.That(afterIndexes.Count).IsEqualTo(4);
            await Assert.That(after.Length).IsEqualTo(before.Length + 1);
            await Assert.That(MessageIdOn(after, afterIndexes["m1"])).IsEqualTo("m1");
            await Assert.That(after[afterIndexes["m1"]].Contains("body 1", StringComparison.Ordinal)).IsTrue();

            // The history still reads in insertion order: the edit changed
            // what was said, not when it was said.
            var messages = await store.GetMessagesAsync(sessionId);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(4);
            await Assert.That(messages.Value[0].Id).IsEqualTo("m0");
            await Assert.That(messages.Value[1].Id).IsEqualTo("m1");
            await Assert.That(messages.Value[2].Id).IsEqualTo("m2");
            await Assert.That(messages.Value[3].Id).IsEqualTo("m3");
            await Assert.That(((UserMessage)messages.Value[1]).Content).IsEqualTo("edited body");

            // And the version that was replaced is still in the log, as a
            // shadowed record — nothing was overwritten.
            var log = await store.GetSessionLogAsync(sessionId);
            await Assert.That(log.IsSuccess).IsTrue();
            var bodies = log.Value
                .Where(e => e.Kind == SessionLogEntryKind.Message)
                .Select(e => ((UserMessage)e.Message!).Content)
                .ToList();
            await Assert.That(bodies.Contains("body 1")).IsTrue();
            await Assert.That(bodies.Contains("edited body")).IsTrue();
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
        // The consequence of the place a message holds, and the reason the
        // place is worth an invariant: the rewind cuts at the anchor's place
        // in the conversation. A message that an edit moved to the tail of the
        // history would be the last message, and a rewind anchored on it would
        // drop nothing — the tail of the history would survive a rewind that
        // was asked to remove it.
        var (store, sessionId, path) = await SeedAsync();
        try
        {
            var edited = await store.UpdateMessageAsync(sessionId, TestMessages.User("edited body", id: "m1", sessionId: sessionId, createdAt: Epoch.AddSeconds(1), model: "claude-opus-4"));
            await Assert.That(edited.IsSuccess).IsTrue();

            var rewind = await store.DeleteMessagesAfterAsync(sessionId, "m1");
            await Assert.That(rewind.IsSuccess).IsTrue();

            // m2 and m3 follow m1 in the conversation, so they are what the
            // rewind removes.
            await Assert.That(rewind.Value).IsEqualTo(2);

            // The surface is the prefix...
            var messages = await store.GetMessagesAsync(sessionId);
            await Assert.That(messages.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });

            // ...and the records are still in the file: the rewind is an event,
            // not a rewrite.
            var log = await store.GetSessionLogAsync(sessionId);
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(5);
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Rewind)).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
