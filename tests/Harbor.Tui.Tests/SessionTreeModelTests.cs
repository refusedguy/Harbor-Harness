using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="SessionTreeModel" /> + <see cref="SessionTreePanelModel" />:
///     title-first rows (never the bare hash), fork depth, orphan/cycle notes,
///     date buckets, fuzzy filtering and the Enter-confirm contract.
///     Deterministic — pure state only.
/// </summary>
public class SessionTreeModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SessionTreeSeed Seed(
        string id, string title, string? parent = null, bool current = false, DateTimeOffset? updated = null) =>
        new(id, title, "/repo/proj", "code", "kilo-auto", T0, updated ?? T0, parent, current);

    private static SessionTreePanelModel Open(params SessionTreeSeed[] seeds)
    {
        var model = new SessionTreePanelModel();
        model.Show(SessionTreeModel.BuildEntries(seeds));
        return model;
    }

    [Test]
    public async Task BuildEntries_TitleFirst_ShortIdInDetail()
    {
        var entries = SessionTreeModel.BuildEntries([Seed("abc12345def67890", "Fix login bug")]);

        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Title).IsEqualTo("Fix login bug");
        await Assert.That(entries[0].ShortId).IsEqualTo("abc12345");
        await Assert.That(entries[0].Detail).Contains("abc12345");
        await Assert.That(entries[0].Detail).Contains("code/kilo-auto");
        await Assert.That(entries[0].Title.Contains("abc12345def67890")).IsFalse();
    }

    [Test]
    public async Task BuildEntries_Chain_IndentsChildren()
    {
        var entries = SessionTreeModel.BuildEntries([
            Seed("root", "root session", updated: T0),
            Seed("child", "forked work", parent: "root", updated: T0.AddMinutes(1)),
            Seed("grandchild", "deep dive", parent: "child", updated: T0.AddMinutes(2)),
        ]);

        await Assert.That(entries).Count().IsEqualTo(3);
        await Assert.That(entries[0].Depth).IsEqualTo(0);
        await Assert.That(entries[0].Title).IsEqualTo("root session");
        await Assert.That(entries[1].Depth).IsEqualTo(1);
        await Assert.That(entries[1].Title.StartsWith("  ")).IsTrue();
        await Assert.That(entries[2].Depth).IsEqualTo(2);
    }

    [Test]
    public async Task BuildEntries_Orphan_ShownWithNote()
    {
        var entries = SessionTreeModel.BuildEntries([Seed("orphan", "lost branch", parent: "missing")]);

        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Detail).Contains("missing");
    }

    [Test]
    public async Task BuildEntries_Cycle_TerminatesWithNote()
    {
        var a = Seed("a", "loop a", parent: "b");
        var b = Seed("b", "loop b", parent: "a");
        var entries = SessionTreeModel.BuildEntries([a, b]);

        // Both rows survive (nothing vanishes) and the loop is marked.
        await Assert.That(entries.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(entries.Any(e => e.Detail.Contains("cycle"))).IsTrue();
    }

    [Test]
    public async Task BuildEntries_Untitled_AndCurrentMarker()
    {
        var entries = SessionTreeModel.BuildEntries([
            Seed("s1", "   ", current: true),
        ]);

        await Assert.That(entries[0].Title).Contains("(untitled)");
        await Assert.That(entries[0].Title).Contains("●");
        await Assert.That(entries[0].IsCurrent).IsTrue();
    }

    [Test]
    public async Task DateBucket_GroupsTodayYesterdayWeekOlder()
    {
        var now = DateTimeOffset.Now;
        await Assert.That(SessionTreeModel.DateBucket(now)).IsEqualTo("1 · Today");
        await Assert.That(SessionTreeModel.DateBucket(now.AddDays(-1))).IsEqualTo("2 · Yesterday");
        await Assert.That(SessionTreeModel.DateBucket(now.AddDays(-3))).IsEqualTo("3 · Previous 7 days");
        await Assert.That(SessionTreeModel.DateBucket(now.AddDays(-30))).IsEqualTo("4 · Older");
    }

    [Test]
    public async Task SetQuery_FiltersByTitle_AndById()
    {
        var model = Open(
            Seed("aaa11111", "Fix login bug"),
            Seed("bbb22222", "Write docs"));

        model.SetQuery("login");
        await Assert.That(model.Results).Count().IsEqualTo(1);
        await Assert.That(model.Results[0].SessionId).IsEqualTo("aaa11111");

        model.SetQuery("bbb22222");
        await Assert.That(model.Results).Count().IsEqualTo(1);
        await Assert.That(model.Results[0].SessionId).IsEqualTo("bbb22222");
    }

    [Test]
    public async Task SetQuery_NoMatch_ClearsSelection()
    {
        var model = Open(Seed("aaa11111", "Fix login bug"));

        model.SetQuery("zzz-no-such-session");

        await Assert.That(model.Results).Count().IsEqualTo(0);
        await Assert.That(model.SelectedIndex).IsEqualTo(-1);
        await Assert.That(model.Confirm()).IsNull();
    }

    [Test]
    public async Task Confirm_ReturnsSelectedSessionId_ForOpen()
    {
        var model = Open(
            Seed("aaa11111", "Fix login bug", updated: T0),
            Seed("bbb22222", "Write docs", updated: T0.AddMinutes(1)));
        model.MoveDown();

        var confirmed = model.Confirm();

        await Assert.That(confirmed).IsNotNull();
        await Assert.That(confirmed!.SessionId).IsEqualTo("bbb22222");
    }

    [Test]
    public async Task Confirm_Hidden_ReturnsNull()
    {
        var model = new SessionTreePanelModel();

        await Assert.That(model.Confirm()).IsNull();
    }

    [Test]
    public async Task MoveDown_MoveUp_ClampAtBounds()
    {
        var model = Open(
            Seed("aaa11111", "one"),
            Seed("bbb22222", "two"));

        model.MoveUp();
        await Assert.That(model.SelectedIndex).IsEqualTo(0);

        model.MoveDown();
        model.MoveDown();
        model.MoveDown();
        await Assert.That(model.SelectedIndex).IsEqualTo(1);
    }

    [Test]
    public async Task Hide_ResetsEverything()
    {
        var model = Open(Seed("aaa11111", "one"));
        model.Hide();

        await Assert.That(model.Visible).IsFalse();
        await Assert.That(model.Results).Count().IsEqualTo(0);
        await Assert.That(model.Confirm()).IsNull();
    }
}
