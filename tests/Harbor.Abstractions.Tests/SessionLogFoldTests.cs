// SessionLogFoldTests.cs — the shared fold of a session log (#1784, PR-1).
//
// The fold is the ONE place that decides what "the surface" is, so its rules
// are pinned here as behaviour rather than as prose:
//
//   1. current by id      — the last record with an id is current; earlier
//                           versions of the same id are shadowed;
//   2. rewind             — a rewind event hides the records between the
//                           anchor and itself, and NOTHING written after it;
//   3. log-only           — a rewind event is in the log and in no surface.
//
// Everything here is a pure structure: no file, no store, no clock.

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Abstractions.Tests;

public class SessionLogFoldTests
{
    /// <summary>
    ///     A log with no rewind event folds to itself: every message is
    ///     current, nothing is shadowed.
    /// </summary>
    [Test]
    public async Task Fold_WithoutRewind_KeepsEveryMessageCurrent()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "hello"),
            LogEntryFixtures.Message(1, "m2", "world"),
        ]);

        await Assert.That(surface.Current.Count).IsEqualTo(2);
        await Assert.That(surface.Shadowed).IsEmpty();
        await Assert.That(surface.LogOnly).IsEmpty();
        await Assert.That(surface.Warnings).IsEmpty();
    }

    /// <summary>
    ///     The example from the design: a rewind at m2 shadows m3, and the
    ///     message written AFTER the rewind stays current.
    /// </summary>
    [Test]
    public async Task Fold_Rewind_HidesTheTailAndKeepsWhatFollowsIt()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "first"),
            LogEntryFixtures.Message(1, "m2", "second"),
            LogEntryFixtures.Message(2, "m3", "third"),
            Rewind(3, "r1", "m2"),
            LogEntryFixtures.Message(4, "m4", "after the rewind"),
        ]);

        await Assert.That(surface.Current.Select(m => m.Id).ToArray())
            .IsEquivalentTo(new[] { "m1", "m2", "m4" });
        await Assert.That(surface.Shadowed.Select(e => e.Id).ToArray())
            .IsEquivalentTo(new[] { "m3" });
        await Assert.That(surface.LogOnly.Select(e => e.Id).ToArray())
            .IsEquivalentTo(new[] { "r1" });
    }

    /// <summary>
    ///     The rewind event itself is log-only: it was never a message, so it
    ///     is in no surface and is not counted as one.
    /// </summary>
    [Test]
    public async Task Fold_RewindEvent_IsLogOnlyAndNeverCurrent()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "first"),
            Rewind(1, "r1", "m1"),
        ]);

        await Assert.That(surface.LogOnly.Count).IsEqualTo(1);
        await Assert.That(surface.LogOnly[0].State).IsEqualTo(SessionLogEntryState.LogOnly);
        await Assert.That(surface.Current.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     An EDIT does not replace the record: the previous version stays in
    ///     the log as a shadowed record and the new one is current. This is
    ///     the rule that makes "the agent's text never disappears" true for
    ///     the in-place-edit path.
    /// </summary>
    [Test]
    public async Task Fold_EditedMessage_KeepsTheEarlierVersionAsShadowed()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "the original text"),
            LogEntryFixtures.Message(1, "m1", "the edited text"),
        ]);

        await Assert.That(surface.Current.Count).IsEqualTo(1);
        await Assert.That(((UserMessage)surface.Current[0]).Content).IsEqualTo("the edited text");
        await Assert.That(surface.Shadowed.Count).IsEqualTo(1);
        await Assert.That(((UserMessage)surface.Shadowed[0].Message!).Content)
            .IsEqualTo("the original text");
    }

    /// <summary>
    ///     A rewind anchors on the anchor's CURRENT version, so an edited
    ///     anchor is not removed by the rewind that names it.
    /// </summary>
    [Test]
    public async Task Fold_RewindOnAnEditedAnchor_KeepsTheAnchorCurrent()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "the original text"),
            LogEntryFixtures.Message(1, "m1", "the edited text"),
            Rewind(2, "r1", "m1"),
        ]);

        await Assert.That(surface.Current.Count).IsEqualTo(1);
        await Assert.That(((UserMessage)surface.Current[0]).Content).IsEqualTo("the edited text");
        await Assert.That(surface.Warnings).IsEmpty();
    }

    /// <summary>
    ///     A rewind whose anchor cannot be found removes nothing and says so.
    ///     Failing open is the deliberate choice: hiding the wrong records is
    ///     not reversible for the user, showing one record too many is.
    /// </summary>
    [Test]
    public async Task Fold_RewindWithUnknownAnchor_RemovesNothingAndWarns()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "first"),
            LogEntryFixtures.Message(1, "m2", "second"),
            Rewind(2, "r1", "m-does-not-exist"),
        ]);

        await Assert.That(surface.Current.Count).IsEqualTo(2);
        await Assert.That(surface.Warnings.Count).IsEqualTo(1);
        await Assert.That(surface.Warnings[0]).Contains("m-does-not-exist");
    }

    /// <summary>
    ///     A rewind anchored on a message an EARLIER rewind already removed
    ///     removes nothing: the anchor is not in the surface, so there is no
    ///     surface position to cut at.
    /// </summary>
    [Test]
    public async Task Fold_RewindOnAShadowedAnchor_RemovesNothingAndWarns()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "first"),
            LogEntryFixtures.Message(1, "m2", "second"),
            LogEntryFixtures.Message(2, "m3", "third"),
            Rewind(3, "r1", "m1"),
            Rewind(4, "r2", "m3"),
        ]);

        await Assert.That(surface.Current.Select(m => m.Id).ToArray())
            .IsEquivalentTo(new[] { "m1" });
        await Assert.That(surface.Warnings.Count).IsEqualTo(1);
        await Assert.That(surface.Warnings[0]).Contains("r2");
    }

    /// <summary>
    ///     Two rewinds in a row, the second anchored EARLIER than the first,
    ///     cut the surface back to the earliest anchor — "roll back further".
    /// </summary>
    [Test]
    public async Task Fold_TwoRewinds_CutBackToTheEarliestAnchor()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "first"),
            LogEntryFixtures.Message(1, "m2", "second"),
            LogEntryFixtures.Message(2, "m3", "third"),
            Rewind(3, "r1", "m2"),
            Rewind(4, "r2", "m1"),
        ]);

        await Assert.That(surface.Current.Select(m => m.Id).ToArray())
            .IsEquivalentTo(new[] { "m1" });
        await Assert.That(surface.Shadowed.Count).IsEqualTo(2);
    }

    /// <summary>
    ///     The fold classifies every record it was given, and the classified
    ///     entries are the same records — a caller can read the log with the
    ///     classification attached.
    /// </summary>
    [Test]
    public async Task Fold_ClassifiesEveryEntryItWasGiven()
    {
        SessionSurface surface = SessionLogFold.Fold(
        [
            LogEntryFixtures.Message(0, "m1", "first"),
            LogEntryFixtures.Message(1, "m2", "second"),
            Rewind(2, "r1", "m1"),
        ]);

        await Assert.That(surface.Entries.Count).IsEqualTo(3);
        await Assert.That(surface.Entries.All(e => e.State is not null)).IsTrue();
        await Assert.That(surface.Entries[0].State).IsEqualTo(SessionLogEntryState.Current);
        await Assert.That(surface.Entries[1].State).IsEqualTo(SessionLogEntryState.Shadowed);
        await Assert.That(surface.Entries[2].State).IsEqualTo(SessionLogEntryState.LogOnly);
    }

    /// <summary>
    ///     An empty log folds to an empty surface without complaining.
    /// </summary>
    [Test]
    public async Task Fold_EmptyLog_FoldsToAnEmptySurface()
    {
        SessionSurface surface = SessionLogFold.Fold([]);

        await Assert.That(surface.Entries).IsEmpty();
        await Assert.That(surface.Current).IsEmpty();
        await Assert.That(surface.Shadowed).IsEmpty();
        await Assert.That(surface.LogOnly).IsEmpty();
        await Assert.That(surface.Warnings).IsEmpty();
    }

    private static SessionLogEntry Rewind(int position, string id, string anchorId) => new()
    {
        Kind = SessionLogEntryKind.Rewind,
        Id = id,
        Position = position,
        AnchorId = anchorId,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };
}
