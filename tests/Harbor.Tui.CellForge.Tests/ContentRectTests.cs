using Harbor.Tui.CellForge.Generic;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ContentRect discipline (#1203, part of epic #1195): every decorated
/// container exposes its content area (ratatui <c>Block::inner</c> canon —
/// <c>block.render(area)</c>, then the child renders into
/// <c>block.inner(area)</c>) and children derive geometry from it instead of
/// recomputing the parent's decor by hand.
/// </summary>
public class ContentRectTests
{
    // ── shared subtraction ──────────────────────────────────────────────

    [Test]
    public async Task Inset_ShrinksEachSide()
    {
        var content = PanelChrome.Inset(new Rect(5, 7, 40, 20), 2, 1, 1, 0);
        await Assert.That(content).IsEqualTo(new Rect(7, 8, 37, 19));
    }

    [Test]
    public async Task Inset_Zero_IsIdentity()
    {
        var outer = new Rect(3, 4, 10, 6);
        await Assert.That(PanelChrome.Inset(outer, 0, 0, 0, 0)).IsEqualTo(outer);
    }

    [Test]
    public async Task Inset_OverInset_ClampsToEmpty()
    {
        var content = PanelChrome.Inset(new Rect(0, 0, 2, 2), 1, 1, 1, 1);
        await Assert.That(content.X).IsEqualTo(1);
        await Assert.That(content.Y).IsEqualTo(1);
        await Assert.That(content.Width).IsEqualTo(0);
        await Assert.That(content.Height).IsEqualTo(0);
    }

    // ── BorderPanel ─────────────────────────────────────────────────────

    [Test]
    public async Task BorderPanel_ContentRect_SubtractsFrame()
    {
        var panel = new BorderPanel("p", 4, 2) { Rect = new Rect(0, 0, 20, 6) };
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(1, 1, 18, 4));
    }

    [Test]
    public async Task BorderPanel_ContentRect_TitleNeedsNoExtraInset()
    {
        var panel = new BorderPanel("p", 4, 2, title: "Chat") { Rect = new Rect(0, 0, 20, 6) };
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(1, 1, 18, 4));
    }

    // ── ComposerPanel ───────────────────────────────────────────────────

    [Test]
    public async Task ComposerPanel_ChromeOn_ReservesTopAndBottomRows()
    {
        var panel = new ComposerPanel("c", new ComposerController(), 10, 4) { ShowChrome = true };
        panel.Rect = new Rect(0, 0, 10, 4);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 1, 10, 2));
    }

    [Test]
    public async Task ComposerPanel_ChromeOff_IsIdentity()
    {
        var panel = new ComposerPanel("c", new ComposerController(), 10, 3);
        panel.Rect = new Rect(0, 0, 10, 3);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 0, 10, 3));
    }

    [Test]
    public async Task ComposerPanel_ChromeOn_ShortRect_FallsBackToLegacy()
    {
        var panel = new ComposerPanel("c", new ComposerController(), 10, 3) { ShowChrome = true };
        panel.Rect = new Rect(0, 0, 10, 3);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 0, 10, 3));
    }

    [Test]
    public async Task ComposerPanel_ChromeOn_PaintsTextInsideContent()
    {
        var composer = new ComposerController();
        composer.Buffer.InsertText("hello");
        var panel = new ComposerPanel("c", composer, 10, 4) { ShowChrome = true };
        panel.Rect = new Rect(0, 0, 10, 4);

        var buffer = TestRender.Buffer(10, 4);
        panel.Paint(buffer);

        string art = TestRender.Art(buffer);
        var rows = art.Split('\n');
        await Assert.That(rows[0]).Contains("INPUT");
        await Assert.That(rows[1]).Contains("hello");
        await Assert.That(rows[3]).IsEqualTo(new string('─', 10));
    }

    // ── StatusPanel ─────────────────────────────────────────────────────

    [Test]
    public async Task StatusPanel_ContentRect_IsIdentity()
    {
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var panel = new StatusPanel("s", status, 10, 1) { ShowChrome = true };
        panel.Rect = new Rect(0, 0, 30, 1);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 0, 30, 1));
    }

    // ── SideBarPanel ────────────────────────────────────────────────────

    [Test]
    public async Task SideBarPanel_ContentRect_SubtractsHairlineTitleAndMargin()
    {
        var panel = new SideBarPanel("sb");
        panel.Rect = new Rect(5, 7, 40, 20);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(7, 8, 37, 19));
    }

    // ── Dock ────────────────────────────────────────────────────────────

    [Test]
    public async Task DockPanel_ContentRect_IsIdentity()
    {
        var panel = new CellForgeDockPanel("d", TuiPanelPlacement.Left, 5);
        panel.Rect = new Rect(0, 0, 30, 10);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 0, 30, 10));
    }

    // ── Generic ─────────────────────────────────────────────────────────

    [Test]
    public async Task GenericContentPanel_Titled_ReservesTitleRow()
    {
        var panel = new GenericContentPanel(new GenericNodeSpec { Id = "g", Title = "Pane" });
        panel.Rect = new Rect(0, 0, 20, 6);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 1, 20, 5));
    }

    [Test]
    public async Task GenericContentPanel_Untitled_IsIdentity()
    {
        var panel = new GenericContentPanel(new GenericNodeSpec { Id = "g" });
        panel.Rect = new Rect(0, 0, 20, 6);
        await Assert.That(panel.ContentRect).IsEqualTo(new Rect(0, 0, 20, 6));
    }
}
