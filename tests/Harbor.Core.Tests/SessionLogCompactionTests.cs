// SessionLogCompactionTests.cs — compaction against the append-only log
// (#1752).
//
// WHAT IS PINNED HERE
// -------------------
// The premise of #1752 was that compaction overwrites the agent's text. It
// does not: compaction APPENDS a summary and hides the folded messages from
// the MODEL CONTEXT. What it did overwrite was the record of an EDIT — and
// that is now append-only too (the JSONL store's UpdateMessageAsync).
//
// So the shape worth pinning is:
//
//   * the log keeps every message the agent produced, including the ones the
//     summary folded — "what did it actually say before the compaction?" has
//     an answer;
//   * the summary is IN the log as a record of its own;
//   * the guard over the log says nothing was lost;
//   * and the model's context is the compacted view: the summary plus the
//     kept tail, with the folded messages out of it.
//
// The last one is the DOMAIN fold (CompactionPolicy) and it is composed on
// top of the storage fold: surface = compactionFold(storageFold(log)). The
// two folds are separate on purpose, and this test is where they meet.

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     Compaction against the append-only session log: the summary is written
///     as a new record, the text it replaces stays in the log, and the model
///     sees the compacted view of it.
/// </summary>
public class SessionLogCompactionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     A session with four messages, and the summary that replaces the
    ///     first two of them — built the way <c>CompactionService</c> builds
    ///     it: an assistant message flagged as a summary, carrying the id of
    ///     the first message kept verbatim.
    /// </summary>
    private static async Task<(JsonlSessionStore Store, string SessionId, AssistantMessage Summary)>
        SeedAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-test-1752-compact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);

        var session = (await store.CreateAsync("/test", "code", "anthropic", "claude-opus-4")).Value;
        for (int i = 0; i < 4; i++)
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

        var summary = TestMessages.Assistant(
            "the first two messages, folded",
            id: "summary-1",
            sessionId: session.Id,
            createdAt: Epoch.AddSeconds(10),
            model: "claude-opus-4",
            isSummary: true,
            summaryFirstKeptId: "m2");

        return (store, session.Id, summary);
    }

    /// <summary>
    ///     The compaction writes the summary as a NEW record and keeps every
    ///     message that was folded into it. The log grew; nothing was
    ///     overwritten.
    /// </summary>
    [Test]
    public async Task Compaction_KeepsTheFoldedText_AndWritesTheSummary()
    {
        var (store, sessionId, summary) = await SeedAsync();
        try
        {
            var before = await store.GetSessionLogAsync(sessionId);
            await Assert.That(before.IsSuccess).IsTrue();

            // What CompactionBehavior does: append the summary. Nothing is
            // rewritten, and nothing is removed.
            var appended = await store.AppendMessageAsync(sessionId, summary);
            await Assert.That(appended.IsSuccess).IsTrue();

            var log = await store.GetSessionLogAsync(sessionId);
            await Assert.That(log.IsSuccess).IsTrue();

            // Five records now: the four messages and the summary.
            await Assert.That(log.Value.Count(e => e.Kind == SessionLogEntryKind.Message)).IsEqualTo(5);

            // The text the summary replaced is still in the log — that is the
            // answer to "what did the agent actually say before the
            // compaction?", and it is why compaction may never rewrite.
            var bodies = log.Value
                .Where(e => e.Kind == SessionLogEntryKind.Message)
                .Select(e => e.Message!)
                .ToList();
            await Assert.That(bodies.Any(m => m.Id == "m0")).IsTrue();
            await Assert.That(bodies.Any(m => m.Id == "m1")).IsTrue();

            // And the summary is one of them.
            var summaryInLog = bodies.Single(m => m.Id == "summary-1") as AssistantMessage;
            await Assert.That(summaryInLog is not null).IsTrue();
            await Assert.That(summaryInLog!.IsSummary).IsTrue();

            // The guard over the log says nothing was lost.
            var after = await store.GetSessionLogAsync(sessionId);
            await Assert.That(SessionLogGuard.IsLossless(before.Value, after.Value)).IsTrue();
            await Assert.That(SessionLogGuard.FindLostRecords(before.Value, after.Value)).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    /// <summary>
    ///     The model's context is the compacted view — the summary plus the
    ///     kept tail — while the log still holds the messages that view
    ///     folded away.
    /// </summary>
    [Test]
    public async Task Compaction_HidesTheFoldedMessagesFromTheModel_NotFromTheLog()
    {
        var (store, sessionId, summary) = await SeedAsync();
        try
        {
            var appended = await store.AppendMessageAsync(sessionId, summary);
            await Assert.That(appended.IsSuccess).IsTrue();

            // What the store hands over: the surface — every message the log
            // still shows. The compaction's hiding is the domain fold's job,
            // and it happens on top of this one.
            var surface = await store.GetMessagesAsync(sessionId);
            await Assert.That(surface.IsSuccess).IsTrue();
            await Assert.That(surface.Value.Count).IsEqualTo(5);

            // What the MODEL sees: the summary plus the tail it kept. The two
            // messages the summary folded are out of the model's context...
            var modelContext = CompactionPolicy.MaterializeCompactedView(surface.Value);
            await Assert.That(modelContext.Count).IsEqualTo(3);
            await Assert.That(modelContext[0].Id).IsEqualTo("summary-1");
            await Assert.That(modelContext.Select(m => m.Id)).IsEquivalentTo(new[] { "summary-1", "m2", "m3" });

            // ...and they are still in the log, where an incident review can
            // find them.
            var log = await store.GetSessionLogAsync(sessionId);
            await Assert.That(log.Value.Any(e => e.Id == "m0" && e.Kind == SessionLogEntryKind.Message)).IsTrue();
            await Assert.That(log.Value.Any(e => e.Id == "m1" && e.Kind == SessionLogEntryKind.Message)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory()))
                Directory.Delete(store.GetRootDirectory(), true);
        }
    }
}
