using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1483: the session sidebar paints pinned rows first and decodes the
///     pin/rename/delete/scope keys through <c>SessionSidebarState</c>. The
///     rendered first data row is the golden assertion for the pinned ordering.
/// </summary>
public class SessionSidebarPanelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(string id, string title) =>
        new(SessionId.Create(id), title, T0, T0, "active");

    private static PanelContext Ctx() => new(
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Sessions =
                [
                    Session("aaa11111", "Beta"),
                    Session("bbb22222", "Alpha"),
                ],
            },
        },
        60,
        20);

    [Test]
    public async Task Pin_FloatsSelectedRowToTop_GoldenFirstDataRow()
    {
        var panel = new CellForgeSessionSidebarPanel();
        var ctx = Ctx();

        // Seed the provider-local rows, then select the second session and pin it.
        _ = panel.Build(ctx);
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Down), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('p'), ctx)).IsTrue();

        var rows = WidgetFixtures.Rows(panel.Build(ctx));
        await Assert.That(rows[2]).Contains("Alpha");
        await Assert.That(rows[3]).Contains("Beta");
    }

    [Test]
    public async Task Delete_ArmEscThenArm_DoesNotDelete_UntilSecondCommit()
    {
        var panel = new CellForgeSessionSidebarPanel();
        var ctx = Ctx();
        _ = panel.Build(ctx);

        await Assert.That(panel.OnKey(UiKey.ForChar('d'), ctx)).IsTrue();
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Escape), ctx)).IsTrue();
        await Assert.That(panel.OnKey(UiKey.ForChar('d'), ctx)).IsTrue();

        var rows = WidgetFixtures.Rows(panel.Build(ctx));
        await Assert.That(rows.Any(r => r.Contains("Beta"))).IsTrue();
        await Assert.That(rows.Any(r => r.Contains("Alpha"))).IsTrue();
    }

    [Test]
    public async Task ScopeKey_TogglesScopeInRenderedFooter()
    {
        var panel = new CellForgeSessionSidebarPanel();
        var ctx = Ctx();
        _ = panel.Build(ctx);

        await Assert.That(panel.OnKey(UiKey.ForChar('s'), ctx)).IsTrue();

        var rows = WidgetFixtures.Rows(panel.Build(ctx));
        await Assert.That(rows[rows.Count - 1]).Contains("scope:cwd");
    }
}
