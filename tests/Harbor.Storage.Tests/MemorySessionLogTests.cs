// MemorySessionLogTests.cs — the append-only session log on the in-memory
// backend (#1752).
//
// The in-memory store is the backend the shared surface tests run against, so
// it has to fold the same log the file-backed store does: the same events,
// the same shadowed records, the same surface. These tests pin that parity —
// the two backends must not drift into two different notions of "the current
// state".

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Memory;
using Harbor.TestKit;

namespace Harbor.Storage.Tests;

/// <summary>
///     The in-memory store keeps a LOG too: a rewind writes an event and an
///     edit writes a new version, and neither destroys a record.
/// </summary>
public class MemorySessionLogTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(MemorySessionStore Store, string SessionId)> SeedAsync(
        MemorySessionStore store,
        int count = 4)
    {
        var created = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
        for (int i = 0; i < count; i++)
        {
            var message = TestMessages.User(
                $"body {i}",
                id: $"m{i}",
                sessionId: created.Id,
                createdAt: Epoch.AddSeconds(i));
            var appended = await store.AppendMessageAsync(created.Id, message);
            await Assert.That(appended).IsSuccessful();
        }

        return (store, created.Id);
    }

    /// <summary>
    ///     A rewind takes the tail out of the surface and leaves the records in
    ///     the log, with the event that says so.
    /// </summary>
    [Test]
    public async Task Rewind_AppendsAnEvent_AndTheTailStaysInTheLog()
    {
        var store = new MemorySessionStore();
        var (seeded, sessionId) = await SeedAsync(store);

        var before = await seeded.GetSessionLogAsync(sessionId);
        await Assert.That(before).IsSuccessful();
        await Assert.That(before.Value.Count).IsEqualTo(4);

        var rewind = await seeded.RewindMessagesAfterAsync(sessionId, "m1");
        await Assert.That(rewind).IsSuccessful();
        await Assert.That(rewind.Value).IsEqualTo(2);

        var log = await seeded.GetSessionLogAsync(sessionId);
        await Assert.That(log).IsSuccessful();
        await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(4);
        await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Rewind)).IsEqualTo(1);

        var surface = await seeded.GetMessagesAsync(sessionId);
        await Assert.That(surface.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });

        await Assert.That(SessionLogGuard.IsLossless(before.Value, log.Value)).IsTrue();
    }

    /// <summary>
    ///     An edit appends a new version; the version before it stays in the
    ///     log, shadowed.
    /// </summary>
    [Test]
    public async Task Update_AppendsANewVersion_AndTheOldTextStaysInTheLog()
    {
        var store = new MemorySessionStore();
        var (seeded, sessionId) = await SeedAsync(store);

        var before = await seeded.GetSessionLogAsync(sessionId);

        var edited = await seeded.UpdateMessageAsync(
            sessionId,
            TestMessages.User(
                "the edited body",
                id: "m1",
                sessionId: sessionId,
                createdAt: Epoch.AddSeconds(1)));
        await Assert.That(edited).IsSuccessful();

        var log = await seeded.GetSessionLogAsync(sessionId);
        await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(5);

        var bodies = log.Value
            .Where(e => e.Kind == SessionLogEntryKind.Message)
            .Select(e => ((UserMessage)e.Message!).Content)
            .ToList();
        await Assert.That(bodies.Contains("body 1")).IsTrue();
        await Assert.That(bodies.Contains("the edited body")).IsTrue();

        var surface = await seeded.GetMessagesAsync(sessionId);
        await Assert.That(surface.Value.Count).IsEqualTo(4);
        await Assert.That(((UserMessage)surface.Value.Single(m => m.Id == "m1")).Content)
            .IsEqualTo("the edited body");

        await Assert.That(SessionLogGuard.IsLossless(before.Value, log.Value)).IsTrue();
    }

    /// <summary>
    ///     The old verb still works, and it still means "rewind to here" — it
    ///     destroys nothing.
    /// </summary>
    [Test]
    public async Task DeleteMessagesAfter_ForwardsToTheAppendOnlyRewind()
    {
        var store = new MemorySessionStore();
        var (seeded, sessionId) = await SeedAsync(store);

        var reverted = await seeded.DeleteMessagesAfterAsync(sessionId, "m1");
        await Assert.That(reverted).IsSuccessful();
        await Assert.That(reverted.Value).IsEqualTo(2);

        var surface = await seeded.GetMessagesAsync(sessionId);
        await Assert.That(surface.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "m0", "m1" });

        var log = await seeded.GetSessionLogAsync(sessionId);
        await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(4);
    }

    /// <summary>
    ///     The prune is the one verb that destroys records.
    /// </summary>
    [Test]
    public async Task Purge_RemovesTheRecords()
    {
        var store = new MemorySessionStore();
        var (seeded, sessionId) = await SeedAsync(store);

        var purged = await seeded.PurgeMessagesAfterAsync(sessionId, "m1");
        await Assert.That(purged).IsSuccessful();
        await Assert.That(purged.Value).IsEqualTo(2);

        var log = await seeded.GetSessionLogAsync(sessionId);
        await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(2);
    }
}
