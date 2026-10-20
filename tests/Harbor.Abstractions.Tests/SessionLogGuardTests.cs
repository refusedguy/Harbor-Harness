// SessionLogGuardTests.cs — the "the agent's text never disappears" guard
// (#1784, PR-1), with the non-vacuous control it is worthless without.
//
// THE INVARIANT UNDER TEST
// -------------------------
// After a mutating store verb, the MULTISET of message-record fingerprints in
// the log still contains the one it had before.
//
// The guard is only worth having if it can FAIL, so this file is written
// around three samples:
//
//   compressing     the tail is physically dropped from the log — today's
//                   destructive rewind. The guard MUST catch this.
//   context-folding the tail is hidden from what the model sees, but every
//                   record stays in the log — what compaction does. The guard
//                   MUST NOT report a loss: compaction does not destroy text,
//                   it appends a summary and hides the folded messages from
//                   the model context only.
//   shadowing       the tail leaves the SURFACE and stays in the log — what
//                   the append-only rewind does. The guard MUST NOT report a
//                   loss either.
//
// The negative control is the point: a guard that cannot fail is a comment
// with an assertion in it.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Abstractions.Tests;

public class SessionLogGuardTests
{
    /// <summary>
    ///     The NEGATIVE control: a sample that physically drops the tail —
    ///     today's destructive rewind — is caught by the guard.
    /// </summary>
    [Test]
    public async Task Guard_CatchesACompressingSample()
    {
        IReadOnlyList<SessionLogEntry> before =
        [
            Message(0, "m1", "the agent said this"),
            Message(1, "m2", "and this"),
            Message(2, "m3", "and this too"),
        ];

        // The compression: the tail is gone from the log, not merely hidden.
        IReadOnlyList<SessionLogEntry> after = [before[0]];

        IReadOnlyList<string> lost = SessionLogGuard.FindLostRecords(before, after);

        await Assert.That(lost.Count).IsEqualTo(2)
            .Because(
                "The guard exists to catch a store that physically drops records. If a compressing "
                + "sample came out clean, the guard would be enforcing nothing and the whole "
                + "append-only model would be decoration.");
    }

    /// <summary>
    ///     The CONTEXT-FOLDING sample — what compaction does — must NOT be
    ///     reported as a loss. Compaction appends a summary message and hides
    ///     the folded messages from the MODEL CONTEXT; the records are in the
    ///     log before and after it.
    /// </summary>
    [Test]
    public async Task Guard_AcceptsAContextFoldingSample()
    {
        IReadOnlyList<SessionLogEntry> before =
        [
            Message(0, "m1", "the agent said this"),
            Message(1, "m2", "and this"),
        ];

        // Compaction: a summary is APPENDED and the folded messages leave the
        // model's context. Nothing is removed from the log.
        IReadOnlyList<SessionLogEntry> after =
        [
            before[0],
            before[1],
            Message(2, "summary-1", "a summary of the two messages above"),
        ];

        await Assert.That(SessionLogGuard.IsLossless(before, after)).IsTrue()
            .Because(
                "Compaction does not destroy text — it appends a summary and hides the folded "
                + "messages from the model context only. A guard that reported a loss here would "
                + "be guarding against the wrong thing: the design's own premise (compaction "
                + "hides, rewind and in-place edit destroy) says the two paths are different.");
    }

    /// <summary>
    ///     The SHADOWING sample — what the append-only rewind does — must not
    ///     be reported as a loss either: the records leave the surface and
    ///     stay in the log.
    /// </summary>
    [Test]
    public async Task Guard_AcceptsAShadowingSample()
    {
        IReadOnlyList<SessionLogEntry> before =
        [
            Message(0, "m1", "the agent said this"),
            Message(1, "m2", "and this"),
        ];

        IReadOnlyList<SessionLogEntry> after =
        [
            before[0],
            before[1],
            Rewind(2, "r1", "m1"),
        ];

        await Assert.That(SessionLogGuard.IsLossless(before, after)).IsTrue()
            .Because(
                "The append-only rewind appends an event and leaves every record in the log. The "
                + "guard protects the LOG, not the surface.");
    }

    /// <summary>
    ///     The guard is a MULTISET comparison: two records with the same
    ///     content are still two records, so dropping one of them is a loss.
    /// </summary>
    [Test]
    public async Task Guard_CountsDuplicateRecordsSeparately()
    {
        IReadOnlyList<SessionLogEntry> before =
        [
            Message(0, "m1", "the same text"),
            Message(1, "m2", "the same text"),
        ];

        IReadOnlyList<SessionLogEntry> after = [before[0]];

        await Assert.That(SessionLogGuard.FindLostRecords(before, after).Count).IsEqualTo(1)
            .Because(
                "The invariant is about the multiset, not the set. Two records with the same "
                + "content are two records, and one of them being dropped is a loss of one.");
    }

    /// <summary>
    ///     An EDIT is not a replacement: the previous version and the new one
    ///     are different records, and the guard must be able to tell them
    ///     apart — otherwise it would call the loss of the earlier version a
    ///     non-event.
    /// </summary>
    [Test]
    public async Task Guard_TellsAnEditedVersionFromTheOriginal()
    {
        UserMessage original = (UserMessage)Message(0, "m1", "the original text").Message!;
        UserMessage edited = (UserMessage)Message(1, "m1", "the edited text").Message!;

        await Assert.That(SessionLogGuard.Fingerprint(original))
            .IsNotEqualTo(SessionLogGuard.Fingerprint(edited))
            .Because(
                "The fingerprint is what makes the guard able to see that an in-place edit "
                + "APPENDED a version instead of replacing one. Without the content digest, the "
                + "guard would see two records with one id and call the edit a replacement.");
    }

    /// <summary>
    ///     END-TO-END negative control, at the level of the store contract: the
    ///     destructive behaviour a store has today — the tail physically
    ///     removed from the log — is caught, and the append-only behaviour is
    ///     not.
    /// </summary>
    [Test]
    public async Task Guard_CatchesTheDestructiveStoreAndAcceptsTheAppendOnlyOne()
    {
        var destructive = new DestructiveStore();
        destructive.Seed("m1", "m2", "m3");

        IReadOnlyList<SessionLogEntry> before = destructive.Entries;
        await destructive.DeleteMessagesAfterAsync("s1", "m1");
        IReadOnlyList<SessionLogEntry> after = destructive.Entries;

        await Assert.That(SessionLogGuard.FindLostRecords(before, after).Count).IsEqualTo(2)
            .Because(
                "This is the defect the model removes: a rewind that physically drops the tail "
                + "loses the agent's text, and the guard has to be able to say so.");

        var appendOnly = new AppendOnlyStore();
        appendOnly.Seed("m1", "m2", "m3");

        IReadOnlyList<SessionLogEntry> appendOnlyBefore = appendOnly.Entries;
        await appendOnly.RewindMessagesAfterAsync("s1", "m1");
        IReadOnlyList<SessionLogEntry> appendOnlyAfter = appendOnly.Entries;

        await Assert.That(SessionLogGuard.IsLossless(appendOnlyBefore, appendOnlyAfter)).IsTrue()
            .Because(
                "The append-only rewind appends an event and keeps every record. If the guard "
                + "flagged this, the fix would be impossible to adopt.");

        // ...and the surface really did shrink, so the sample is not vacuous:
        // the two logs differ in what they show, and only in what they show.
        SessionSurface surface = SessionLogFold.Fold(appendOnlyAfter);
        await Assert.That(surface.Current.Count).IsEqualTo(1);
        await Assert.That(surface.Shadowed.Count).IsEqualTo(2);
    }

    private static SessionLogEntry Message(int position, string id, string content) => new()
    {
        Kind = SessionLogEntryKind.Message,
        Id = id,
        Position = position,
        Message = new UserMessage(
            id,
            "s1",
            DateTimeOffset.UnixEpoch,
            content,
            "code",
            "test-model"),
    };

    private static SessionLogEntry Rewind(int position, string id, string anchorId) => new()
    {
        Kind = SessionLogEntryKind.Rewind,
        Id = id,
        Position = position,
        AnchorId = anchorId,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>
    ///     A store that behaves the way the backends behaved until #1784: the
    ///     tail after the anchor is PHYSICALLY REMOVED from the log.
    /// </summary>
    private sealed class DestructiveStore : StoreDouble
    {
        public Task<Result<int>> DeleteMessagesAfterAsync(
            string sessionId, string messageId, CancellationToken ct = default)
        {
            int anchor = -1;

            for (int i = 0; i < Log.Count; i++)
            {
                SessionLogEntry entry = Log[i];

                if (entry.Kind == SessionLogEntryKind.Message && entry.Id == messageId)
                {
                    anchor = i;
                    break;
                }
            }

            int removed = 0;

            for (int i = Log.Count - 1; i > anchor; i--)
            {
                Log.RemoveAt(i);
                removed++;
            }

            return Task.FromResult(Result.Success(removed));
        }
    }

    /// <summary>
    ///     A store that behaves the way the backends will behave after
    ///     #1784: the rewind is an EVENT appended to the log, and no record is
    ///     removed.
    /// </summary>
    private sealed class AppendOnlyStore : StoreDouble
    {
        public Task<Result<int>> RewindMessagesAfterAsync(
            string sessionId, string messageId, CancellationToken ct = default)
        {
            int anchor = -1;

            for (int i = 0; i < Log.Count; i++)
            {
                SessionLogEntry entry = Log[i];

                if (entry.Kind == SessionLogEntryKind.Message && entry.Id == messageId)
                {
                    anchor = i;
                    break;
                }
            }

            int removed = anchor < 0 ? 0 : Log.Count - anchor - 1;

            Log.Add(new SessionLogEntry
            {
                Kind = SessionLogEntryKind.Rewind,
                Id = $"r{Log.Count}",
                Position = Log.Count,
                AnchorId = messageId,
                OccurredAt = DateTimeOffset.UnixEpoch,
                RemovedCount = removed,
            });

            return Task.FromResult(Result.Success(removed));
        }
    }

    /// <summary>
    ///     The parts of the store contract the guard's control does not
    ///     exercise. A member that is not under test says so loudly instead of
    ///     returning a plausible-looking value.
    /// </summary>
    private abstract class StoreDouble : ISessionStore
    {
        protected List<SessionLogEntry> Log { get; } = [];

        /// <summary>
        ///     A SNAPSHOT of the log, not the live list. The guard compares two
        ///     moments, so handing it the same mutable list twice would compare
        ///     a list with itself and report nothing — which is exactly the
        ///     vacuous control this file exists to avoid.
        /// </summary>
        public IReadOnlyList<SessionLogEntry> Entries => Log.ToArray();

        public void Seed(params string[] messageIds)
        {
            for (int i = 0; i < messageIds.Length; i++)
            {
                Log.Add(new SessionLogEntry
                {
                    Kind = SessionLogEntryKind.Message,
                    Id = messageIds[i],
                    Position = i,
                    Message = new UserMessage(
                        messageIds[i],
                        "s1",
                        DateTimeOffset.UnixEpoch,
                        $"the text of {messageIds[i]}",
                        "code",
                        "test-model"),
                });
            }
        }

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<Session>>> ListAsync(
            string? projectId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> AppendMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> UpdateMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(
            string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<SessionMetadata>> GetStatsAsync(
            string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> UpdateStatsAsync(
            string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
