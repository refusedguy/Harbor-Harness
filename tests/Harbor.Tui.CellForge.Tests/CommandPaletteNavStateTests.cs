using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class CommandPaletteNavStateTests
{
    private static readonly CommandItem[] Commands =
    [
        new("clear", "Clear session", "wipe the visible transcript"),
        new("fork", "Fork session", "branch from a message"),
        new("model", "Switch model", "pick another provider model", "ctrl+m"),
        new("quit", "Quit", "leave harbor"),
        new("help", "Help", "list commands"),
    ];

    private static CommandPaletteView Open()
    {
        var palette = new CommandPaletteView();
        palette.Show(Commands);
        return palette;
    }

    // List rows start at rect.Y + 2; the entry text paints at rect.X + 1.
    // Selected rows carry Bold (Accent+Bold), plain rows do not (query row
    // at y=1 is Bold too, so only list rows are graded; the seed commands
    // carry no groups, so no section header can false-positive).
    private static bool IsPicked(ScreenBuffer buffer, int y) =>
        buffer.Get(1, y).Style.Attrs.HasFlag(StyleAttr.Bold);

    [Test]
    public async Task Palette_State_MirrorsSelectableCount()
    {
        var palette = new CommandPaletteView();
        palette.Show(Commands);
        await Assert.That(palette.State.Count).IsEqualTo(Commands.Length);
        await Assert.That(palette.State.Selected).IsEqualTo(0);
        palette.Hide();
        await Assert.That(palette.State.Count).IsEqualTo(0);
        await Assert.That(palette.State.Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task Palette_ExternalState_DrivesHighlight()
    {
        var palette = Open();
        palette.State.MoveTo(2);

        var buffer = TestRender.Buffer(60, 12);
        palette.Paint(buffer, new Rect(0, 0, 60, 12), palette.State);

        await Assert.That(IsPicked(buffer, 2)).IsFalse();
        await Assert.That(IsPicked(buffer, 3)).IsFalse();
        await Assert.That(IsPicked(buffer, 4)).IsTrue();
        await Assert.That(IsPicked(buffer, 5)).IsFalse();
    }

    [Test]
    public async Task Palette_SharedState_DrivesTwoPalettes()
    {
        // The #1158 contract: state is external, so one state object can drive
        // two palettes and both paint the same selection.
        var left = Open();
        var right = Open();
        left.State.MoveTo(3);

        var buffer = TestRender.Buffer(60, 12);
        right.Paint(buffer, new Rect(0, 0, 60, 12), left.State);
        await Assert.That(IsPicked(buffer, 5)).IsTrue();
        await Assert.That(IsPicked(buffer, 2)).IsFalse();
    }

    [Test]
    public async Task Palette_ExternalState_ScrollsToSelection()
    {
        var palette = PaletteFixtures.OpenMany(20);
        palette.State.SelectLast();

        // Height 6 → 2 list rows; selection drags the viewport down.
        var buffer = TestRender.Buffer(60, 6);
        palette.Paint(buffer, new Rect(0, 0, 60, 6), palette.State);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("Command 19")).IsTrue();
        await Assert.That(art.Contains("Command 00")).IsFalse();
    }

    [Test]
    public async Task Palette_MismatchedState_FallsBackToLegacy()
    {
        var palette = Open();
        _ = palette.HandleKey(KeyEvent.Simple(KeyCode.Down));
        var foreign = new GenericNavState();
        foreign.SetContent(99);
        foreign.MoveTo(50);

        var buffer = TestRender.Buffer(60, 12);
        palette.Paint(buffer, new Rect(0, 0, 60, 12), foreign);
        await Assert.That(IsPicked(buffer, 3)).IsTrue();
        await Assert.That(IsPicked(buffer, 2)).IsFalse();
    }

    [Test]
    public async Task Palette_LegacyPaint_UnchangedByStatefulPaint()
    {
        var palette = Open();
        palette.State.MoveTo(4);

        // Stateful paint leaves the legacy selection alone: the legacy path
        // still highlights the first entry.
        var stateful = TestRender.Buffer(60, 12);
        palette.Paint(stateful, new Rect(0, 0, 60, 12), palette.State);
        await Assert.That(IsPicked(stateful, 6)).IsTrue();

        var buffer = TestRender.Buffer(60, 12);
        palette.Paint(buffer, new Rect(0, 0, 60, 12));
        await Assert.That(IsPicked(buffer, 2)).IsTrue();
        await Assert.That(IsPicked(buffer, 3)).IsFalse();
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);
        await Assert.That(palette.Viewport.Offset == 0).IsTrue();
    }
}
