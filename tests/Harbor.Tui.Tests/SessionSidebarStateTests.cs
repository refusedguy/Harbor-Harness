using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     State tests for the interactive session sidebar (#1483). Every mutation
///     goes through <see cref="SessionListSearchModel"/>; these tests pin the
///     resulting state transitions, not the panel's key decoding.
/// </summary>
public class SessionSidebarStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SessionListRow Row(string id, string title, bool pinned = false) =>
        new(id, title, "/repo/proj", pinned, T0);

    private static SessionSidebarState Seeded() =>
        SessionSidebarState.Empty with
        {
            Rows =
            [
                Row("aaa", "Alpha"),
                Row("bbb", "Beta"),
                Row("ccc", "Gamma", pinned: true),
            ],
        };

    [Test]
    public async Task TogglePin_FloatsPinnedRowToTop_AndKeepsCursorOnIt()
    {
        var state = Seeded() with { Cursor = 0 };

        var pinned = state.TogglePin("aaa");
        var visible = pinned.VisibleRows();

        await Assert.That(visible[0].SessionId).IsEqualTo("aaa");
        await Assert.That(visible[0].IsPinned).IsTrue();
        await Assert.That(visible[1].SessionId).IsEqualTo("ccc");
        await Assert.That(pinned.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task CommitRename_AcceptsTrimmed_RejectsBlankAndOverlong()
    {
        var state = Seeded() with { Cursor = 0 };

        bool ok = state.BeginRename("  Renamed  ").CommitRename("aaa", out var renamed);
        await Assert.That(ok).IsTrue();
        await Assert.That(renamed.Rows[0].Title).IsEqualTo("Renamed");
        await Assert.That(renamed.RenameDraft).IsNull();

        bool blank = state.BeginRename("   ").CommitRename("aaa", out var afterBlank);
        await Assert.That(blank).IsFalse();
        await Assert.That(afterBlank.Rows[0].Title).IsEqualTo("Alpha");

        string tooLong = new('x', SessionListSearchModel.MaxTitleLength + 1);
        bool overlong = state.BeginRename(tooLong).CommitRename("aaa", out var afterLong);
        await Assert.That(overlong).IsFalse();
        await Assert.That(afterLong.Rows[0].Title).IsEqualTo("Alpha");
    }

    [Test]
    public async Task Delete_IsTwoStep_EscDisarmsWithoutDeleting()
    {
        var state = Seeded() with { Cursor = 1 };

        var armed = state.ArmDelete("bbb");
        await Assert.That(armed.IsDeleteArmed("bbb")).IsTrue();
        await Assert.That(state.ConfirmDelete("bbb", out var premature)).IsFalse();
        await Assert.That(premature.Rows).Count().IsEqualTo(3);

        var cancelled = armed.CancelInteraction();
        await Assert.That(cancelled.DeleteArmedId).IsNull();
        await Assert.That(cancelled.ConfirmDelete("bbb", out var noDelete)).IsFalse();
        await Assert.That(noDelete.Rows).Count().IsEqualTo(3);

        bool deleted = armed.ConfirmDelete("bbb", out var after);
        await Assert.That(deleted).IsTrue();
        await Assert.That(after.Rows).Count().IsEqualTo(2);
        await Assert.That(after.DeleteArmedId).IsNull();
    }

    [Test]
    public async Task ToggleScope_FlipsScope_AndExposesPersistentKey()
    {
        var state = Seeded();

        var cwd = state.ToggleScope();
        await Assert.That(cwd.Scope).IsEqualTo(SessionListScope.Cwd);
        await Assert.That(cwd.VisibleRows(null)).Count().IsEqualTo(3);
        await Assert.That(cwd.VisibleRows("/repo/proj")).Count().IsEqualTo(3);
        await Assert.That(cwd.VisibleRows("/somewhere/else")).Count().IsEqualTo(0);

        await Assert.That(cwd.ToggleScope().Scope).IsEqualTo(SessionListScope.AllProjects);
        await Assert.That(SessionSidebarState.ScopeKey).IsEqualTo("session-list.scope");
    }

    [Test]
    public async Task MergeSessions_PreservesLocalEditsForStillPresentIds()
    {
        var state = Seeded();
        var pinned = state.TogglePin("aaa");
        var renamed = pinned.BeginRename("Alpha two").CommitRename("aaa", out var edited) ? edited : pinned;

        string freshTitle = "Alpha from store";
        var merged = renamed.MergeSessions([Row("aaa", freshTitle), Row("ddd", "Delta")]);

        await Assert.That(merged.Rows).Count().IsEqualTo(2);
        await Assert.That(merged.Rows[0].Title).IsEqualTo("Alpha two");
        await Assert.That(merged.Rows[0].IsPinned).IsTrue();
        await Assert.That(merged.Rows[1].Title).IsEqualTo("Delta");
    }
}
