// CompactionLockTests.cs — the fold that says whether a compaction lock is
// held (#1767).
//
// The fold is the whole "is a compaction running right now?" question, and it
// is a pure function over the session's compaction records. These tests pin
// the four shapes that matter:
//
//   no records          -> no lock
//   start, end          -> no lock (the compaction finished)
//   start               -> the lock is HELD: the crash case
//   start, end, start   -> the lock is HELD again, owned by the newer turn
//
// The crash case is the one the issue is about: a process that died between
// the two records leaves the unmatched start behind, and the fold names it.

using Harbor.Abstractions.Sessions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     The compaction-lock fold: what the session's compaction records say
///     about the lock right now (#1767).
/// </summary>
public class CompactionLockTests
{
    private static SessionCompactionEvent Start(int? turn = 1) => new()
    {
        Kind = SessionCompactionEventKind.Start,
        Turn = turn,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    private static SessionCompactionEvent End(int? turn = 1) => new()
    {
        Kind = SessionCompactionEventKind.End,
        Turn = turn,
        PrunedMessageCount = 0,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    [Test]
    public async Task No_Records_Means_No_Lock()
    {
        SessionCompactionEvent? open = CompactionLock.FindOpenStart([]);

        await Assert.That(open).IsNull();
    }

    [Test]
    public async Task A_Finished_Compaction_Holds_No_Lock()
    {
        SessionCompactionEvent? open = CompactionLock.FindOpenStart([Start(1), End(1)]);

        await Assert.That(open).IsNull();
    }

    [Test]
    public async Task An_Unmatched_Start_Is_The_Orphaned_Lock()
    {
        SessionCompactionEvent? open = CompactionLock.FindOpenStart([Start(7)]);

        await Assert.That(open).IsNotNull();
        await Assert.That(open!.Turn).IsEqualTo(7);
    }

    [Test]
    public async Task A_Second_Start_Takes_The_Lock_Over_From_An_Abandoned_One()
    {
        // The first owner never released the lock, so the second start takes
        // it over: the open lock is the NEWER one, and the older one is the
        // orphan the new start names.
        SessionCompactionEvent? open = CompactionLock.FindOpenStart([Start(1), Start(2)]);

        await Assert.That(open).IsNotNull();
        await Assert.That(open!.Turn).IsEqualTo(2);
    }

    [Test]
    public async Task An_End_Without_A_Start_Releases_Nothing()
    {
        SessionCompactionEvent? open = CompactionLock.FindOpenStart([End(1)]);

        await Assert.That(open).IsNull();
    }

    [Test]
    public async Task The_Lock_Is_Held_Again_After_A_Second_Compaction_Starts()
    {
        SessionCompactionEvent? open = CompactionLock.FindOpenStart([Start(1), End(1), Start(2)]);

        await Assert.That(open).IsNotNull();
        await Assert.That(open!.Turn).IsEqualTo(2);
    }

    [Test]
    public async Task The_Fold_Rejects_A_Null_Event_List()
    {
        await Assert.That(() => CompactionLock.FindOpenStart(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    ///     Every kind of the union is handled — the census is driven by
    ///     REFLECTION, so a kind added to the enum shows up here instead of
    ///     ageing past a hand-written list.
    /// </summary>
    [Test]
    public async Task Every_Kind_Of_The_Union_Is_Answered()
    {
        SessionCompactionEventKind[] kinds = Enum.GetValues<SessionCompactionEventKind>();

        await Assert.That(kinds.Length).IsGreaterThan(0);

        foreach (SessionCompactionEventKind kind in kinds)
        {
            var record = new SessionCompactionEvent
            {
                Kind = kind,
                Turn = 1,
                OccurredAt = DateTimeOffset.UnixEpoch,
            };

            SessionCompactionEvent? open = CompactionLock.FindOpenStart([record]);

            // A start takes the lock; an end releases it. Whatever the kind
            // is, the fold answers without throwing.
            bool expectedOpen = kind == SessionCompactionEventKind.Start;
            await Assert.That(open is not null).IsEqualTo(expectedOpen);
        }
    }
}
