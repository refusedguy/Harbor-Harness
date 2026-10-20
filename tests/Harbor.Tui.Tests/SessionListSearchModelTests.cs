using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="SessionListSearchModel" /> (#1178): instant local
///     filter, 150 ms server debounce, two-tier merge with latest fallback,
///     allProjects/cwd scope over a persistent key, inline pin/rename/delete.
///     Deterministic — pure state only, no sleeps.
/// </summary>
public class SessionListSearchModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SessionListRow Row(
        string id, string title, string dir = "/repo/proj", bool pinned = false) =>
        new(id, title, dir, pinned, T0);

    private static List<SessionListRow> Seeds() =>
    [
        Row("aaa11111", "Fix login bug", "/repo/proj"),
        Row("bbb22222", "Write docs", "/repo/other", pinned: true),
        Row("ccc33333", "Login refactor", "/repo/proj"),
    ];

    [Test]
    public async Task FilterLocal_EmptyQuery_ReturnsAll_PinnedFirst()
    {
        var results = SessionListSearchModel.FilterLocal(Seeds(), string.Empty);

        await Assert.That(results).Count().IsEqualTo(3);
        await Assert.That(results[0].SessionId).IsEqualTo("bbb22222");
    }

    [Test]
    public async Task FilterLocal_MatchesTitleOrId_CaseInsensitive()
    {
        var rows = Seeds();

        var byTitle = SessionListSearchModel.FilterLocal(rows, "LOGIN");
        await Assert.That(byTitle).Count().IsEqualTo(2);

        var byId = SessionListSearchModel.FilterLocal(rows, "bbb22222");
        await Assert.That(byId).Count().IsEqualTo(1);
        await Assert.That(byId[0].Title).IsEqualTo("Write docs");
    }

    [Test]
    public async Task ApplyScope_Cwd_KeepsOnlyCurrentDirectory()
    {
        var rows = Seeds();

        var scoped = SessionListSearchModel.ApplyScope(rows, SessionListScope.Cwd, "/repo/proj/");

        await Assert.That(scoped).Count().IsEqualTo(2);
        await Assert.That(scoped.All(r => r.Directory == "/repo/proj")).IsTrue();
    }

    [Test]
    public async Task ApplyScope_AllProjects_OrEmptyCwd_KeepsEverything()
    {
        var rows = Seeds();

        await Assert.That(SessionListSearchModel.ApplyScope(rows, SessionListScope.AllProjects, "/repo/proj")).Count().IsEqualTo(3);
        await Assert.That(SessionListSearchModel.ApplyScope(rows, SessionListScope.Cwd, string.Empty)).Count().IsEqualTo(3);
    }

    [Test]
    public async Task DebounceDue_FiresAt150Ms()
    {
        await Assert.That(SessionListSearchModel.IsDebounceDue(T0, T0.AddMilliseconds(149))).IsFalse();
        await Assert.That(SessionListSearchModel.IsDebounceDue(T0, T0.AddMilliseconds(150))).IsTrue();
        // The server debounce IS a constant, and the constant IS the subject:
        // the test pins the number the server side waits. A change to the
        // constant recompiles this assertion to the new value and the test
        // fails, so the change is noticed. TUnit's TUnitAssertions0005 reports
        // the constant first argument; see the policy in
        // tests/Directory.Build.props.
#pragma warning disable TUnitAssertions0005 // the server-debounce constant is the subject: the test pins its numeric value
        await Assert.That(SessionListSearchModel.ServerDebounceMs).IsEqualTo(150);
#pragma warning restore TUnitAssertions0005
    }

    [Test]
    public async Task CombineTiered_PendingLocal_ReadyServer_EmptyFallsBackToLatest()
    {
        var local = new List<SessionListRow> { Row("l1", "local") };
        var server = new List<SessionListRow> { Row("s1", "server") };
        var latest = new List<SessionListRow> { Row("n1", "latest") };

        await Assert.That(SessionListSearchModel.CombineTiered(local, server, latest, serverReady: false)[0].SessionId).IsEqualTo("l1");
        await Assert.That(SessionListSearchModel.CombineTiered(local, server, latest, serverReady: true)[0].SessionId).IsEqualTo("s1");
        await Assert.That(SessionListSearchModel.CombineTiered(local, [], latest, serverReady: true)[0].SessionId).IsEqualTo("n1");
    }

    [Test]
    public async Task TogglePin_FlipsFlag()
    {
        var toggled = SessionListSearchModel.TogglePin(Seeds(), "aaa11111");

        await Assert.That(toggled[0].IsPinned).IsTrue();
        await Assert.That(Seeds()[0].IsPinned).IsFalse();
    }

    [Test]
    public async Task TryRename_AcceptsTrimmed_RejectsBlankAndOverlong()
    {
        bool ok = SessionListSearchModel.TryRename(Seeds(), "aaa11111", "  New title  ", out var updated);
        await Assert.That(ok).IsTrue();
        await Assert.That(updated[0].Title).IsEqualTo("New title");

        await Assert.That(SessionListSearchModel.TryRename(Seeds(), "aaa11111", "   ", out _)).IsFalse();
        await Assert.That(SessionListSearchModel.TryRename(Seeds(), "aaa11111", new string('x', 121), out _)).IsFalse();
        await Assert.That(SessionListSearchModel.TryRename(Seeds(), "missing", "Title", out _)).IsFalse();
    }

    [Test]
    public async Task Delete_RemovesRow_OrReportsMissing()
    {
        bool ok = SessionListSearchModel.Delete(Seeds(), "bbb22222", out var updated);
        await Assert.That(ok).IsTrue();
        await Assert.That(updated).Count().IsEqualTo(2);

        await Assert.That(SessionListSearchModel.Delete(Seeds(), "missing", out var unchanged)).IsFalse();
        await Assert.That(unchanged).Count().IsEqualTo(3);
    }

    [Test]
    public async Task ScopePersistKey_IsStable()
    {
        await Assert.That(SessionListSearchModel.ScopePersistKey).IsEqualTo("session-list.scope");
    }
}
