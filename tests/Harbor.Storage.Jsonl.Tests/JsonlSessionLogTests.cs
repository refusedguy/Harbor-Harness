// JsonlSessionLogTests.cs — the append-only session log on the JSONL backend
// (#1752).
//
// WHAT IS PINNED HERE
// -------------------
// The log is the truth and it only grows. Two verbs used to destroy what the
// agent had said:
//
//   * the rewind, which rewrote the file without the tail after the anchor;
//   * the edit, which rewrote the record in place, so the text before the
//     edit was gone.
//
// Both now APPEND: the rewind writes an event that says which messages left
// the surface, and the edit writes a new version. The records they used to
// destroy are still in the file, and the surface is the fold of the log.
//
// The guard is the shared one (Harbor.Abstractions.Sessions.SessionLogGuard):
// the multiset of message fingerprints must still CONTAIN what it held before
// the mutation. A store that loses a record fails it — which is the point.

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The JSONL session log: append-only records, a surface folded from them,
///     and a prune as the only path that destroys anything.
/// </summary>
public class JsonlSessionLogTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonlSessionStore CreateStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"harbor-test-1752-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
    }

    /// <summary>
    ///     A session with four messages, m0..m3, in that order.
    /// </summary>
    private static async Task<(JsonlSessionStore Store, string SessionId)> SeedAsync(
        JsonlSessionStore store,
        int count = 4)
    {
        var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
        for (int i = 0; i < count; i++)
        {
            var append = await store.AppendMessageAsync(
                session.Id,
                TestMessages.User(
                    $"body {i}",
                    id: $"m{i}",
                    sessionId: session.Id,
                    createdAt: Epoch.AddSeconds(i),
                    model: "claude-opus-4"));
            await Assert.That(append.IsSuccess).IsTrue();
        }

        return (store, session.Id);
    }

    /// <summary>
    ///     The rewind leaves the tail in the FILE and records the event that
    ///     took it out of the surface. The text the agent produced after the
    ///     anchor is still there — that is the whole difference between
    ///     "shadowed" and "gone".
    /// </summary>
    [Test]
    public async Task Rewind_AppendsAnEvent_AndTheTailStaysInTheLog()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);

            var before = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(before.IsSuccess).IsTrue();
            await Assert.That(before.Value.Count).IsEqualTo(4);

            var rewind = await seeded.RewindMessagesAfterAsync(sessionId, "m1");
            await Assert.That(rewind.IsSuccess).IsTrue();

            // Two messages left the surface: m2 and m3.
            await Assert.That(rewind.Value).IsEqualTo(2);

            var log = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(log.IsSuccess).IsTrue();

            // The four message records are all still there...
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(4);

            // ...and the rewind is an event of its own, in the log.
            var events = log.Value.Where(e => e.Kind == SessionLogEntryKind.Rewind).ToList();
            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0].AnchorId).IsEqualTo("m1");
            await Assert.That(events[0].RemovedCount).IsEqualTo(2);

            // The surface is the current state: the tail left it.
            var surface = await seeded.GetMessagesAsync(sessionId);
            await Assert.That(surface.IsSuccess).IsTrue();
            await Assert.That(surface.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });

            // And the guard says the log lost nothing.
            var after = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(SessionLogGuard.IsLossless(before.Value, after.Value)).IsTrue();
            await Assert.That(SessionLogGuard.FindLostRecords(before.Value, after.Value)).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The rewind survives a restart: the event is on disk, so a new
    ///     process reading the file folds the same surface.
    /// </summary>
    [Test]
    public async Task Rewind_SurvivesAStoreRecreate()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);
            var rewind = await seeded.RewindMessagesAfterAsync(sessionId, "m1");
            await Assert.That(rewind.IsSuccess).IsTrue();

            var second = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
            var surface = await second.GetMessagesAsync(sessionId);
            await Assert.That(surface.IsSuccess).IsTrue();
            await Assert.That(surface.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });

            // And the text that left the surface is still readable from the log.
            var log = await second.GetSessionLogAsync(sessionId);
            await Assert.That(log.IsSuccess).IsTrue();
            var texts = log.Value
                .Where(e => e.Kind == SessionLogEntryKind.Message)
                .Select(e => ((UserMessage)e.Message!).Content)
                .ToList();
            await Assert.That(texts.Contains("body 3")).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     An edit appends a NEW VERSION. The version before it stays in the
    ///     log — shadowed, not gone — and the surface shows the new one.
    /// </summary>
    [Test]
    public async Task Update_AppendsANewVersion_AndTheOldTextStaysInTheLog()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);

            var before = await seeded.GetSessionLogAsync(sessionId);

            var edited = await seeded.UpdateMessageAsync(
                sessionId,
                TestMessages.User(
                    "the edited body",
                    id: "m1",
                    sessionId: sessionId,
                    createdAt: Epoch.AddSeconds(1),
                    model: "claude-opus-4"));
            await Assert.That(edited.IsSuccess).IsTrue();

            var log = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(log.IsSuccess).IsTrue();

            // Five message records now: the four that were there, plus the new
            // version of m1.
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(5);

            // The old text is still there — twice, in fact: once in the
            // version that was written and once in the version that replaced
            // it.
            var bodies = log.Value
                .Where(e => e.Kind == SessionLogEntryKind.Message)
                .Select(e => ((UserMessage)e.Message!).Content)
                .ToList();
            await Assert.That(bodies.Count(b => b == "body 1")).IsEqualTo(1);
            await Assert.That(bodies.Count(b => b == "the edited body")).IsEqualTo(1);

            // The surface shows the new text, and the message keeps its place.
            var surface = await seeded.GetMessagesAsync(sessionId);
            await Assert.That(surface.IsSuccess).IsTrue();
            await Assert.That(surface.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1", "m2", "m3" });
            await Assert.That(((UserMessage)surface.Value.Single(m => m.Id == "m1")).Content)
                .IsEqualTo("the edited body");

            // The guard says the log lost nothing.
            var after = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(SessionLogGuard.IsLossless(before.Value, after.Value)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     An edit of a message the log never held fails and changes nothing.
    /// </summary>
    [Test]
    public async Task Update_OfAMessageTheLogNeverHeld_FailsAndChangesNothing()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);
            var before = await seeded.GetSessionLogAsync(sessionId);

            var edited = await seeded.UpdateMessageAsync(
                sessionId,
                TestMessages.User("nope", id: "does-not-exist", sessionId: sessionId, model: "claude-opus-4"));

            await Assert.That(edited.IsFailure).IsTrue();
            await Assert.That(edited.Error).Contains("does-not-exist");

            var after = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(after.Value.Count).IsEqualTo(before.Value.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The ordinary read gives the CURRENT state; the log read gives the
    ///     HISTORY. Same store, same moment, two answers.
    /// </summary>
    [Test]
    public async Task TheOrdinaryRead_GivesTheCurrentState_AndTheLogGivesTheHistory()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);
            await seeded.RewindMessagesAfterAsync(sessionId, "m1");

            var surface = await seeded.GetMessagesAsync(sessionId);
            var log = await seeded.GetSessionLogAsync(sessionId);

            await Assert.That(surface.Value.Count).IsEqualTo(2);
            await Assert.That(log.Value.Count).IsEqualTo(5);

            // The log is the whole truth: the messages that left the surface
            // are in it, and so is the event that took them out.
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(4);
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Rewind)).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The prune is the ONE path that destroys records, and it says how
    ///     many it destroyed.
    /// </summary>
    [Test]
    public async Task Purge_IsTheOnlyPathThatRemovesRecords()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);

            var purged = await seeded.PurgeMessagesAfterAsync(sessionId, "m1");
            await Assert.That(purged.IsSuccess).IsTrue();
            await Assert.That(purged.Value).IsEqualTo(2);

            var log = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(log.IsSuccess).IsTrue();

            // The records are gone — the prune is the one verb that is allowed
            // to say so.
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The old name still works and still means "rewind to here" — it
    ///     forwards to the append-only rewind, so it destroys nothing.
    /// </summary>
    [Test]
    public async Task DeleteMessagesAfter_ForwardsToTheAppendOnlyRewind()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);

            var reverted = await seeded.DeleteMessagesAfterAsync(sessionId, "m1");
            await Assert.That(reverted.IsSuccess).IsTrue();
            await Assert.That(reverted.Value).IsEqualTo(2);

            var surface = await seeded.GetMessagesAsync(sessionId);
            await Assert.That(surface.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });

            // The tail is still in the log — the old verb did not destroy it.
            var log = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(4);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A rewind to a message that is not in the log fails, and the log is
    ///     untouched: a rewind that cannot find its anchor must not hide
    ///     anything.
    /// </summary>
    [Test]
    public async Task Rewind_ToAnUnknownAnchor_FailsAndHidesNothing()
    {
        var store = CreateStore(out string root);
        try
        {
            var (seeded, sessionId) = await SeedAsync(store);
            var before = await seeded.GetSessionLogAsync(sessionId);

            var rewind = await seeded.RewindMessagesAfterAsync(sessionId, "no-such-message");
            await Assert.That(rewind.IsFailure).IsTrue();

            var after = await seeded.GetSessionLogAsync(sessionId);
            await Assert.That(after.Value.Count).IsEqualTo(before.Value.Count);

            var surface = await seeded.GetMessagesAsync(sessionId);
            await Assert.That(surface.Value.Count).IsEqualTo(4);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
