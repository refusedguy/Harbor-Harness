using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class DialogSelectListNavStateTests
{
    private static readonly string[] Options =
    [
        "alpha",
        "bravo",
        "charlie",
        "delta",
        "echo",
    ];

    private static DialogSelectList Open()
    {
        var list = new DialogSelectList();
        list.SetItems(Options);
        return list;
    }

    private static DialogSelectList OpenMany(int count)
    {
        var items = new string[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = "Option " + i.ToString("00");
        }

        var list = new DialogSelectList();
        list.SetItems(items);
        return list;
    }

    // Rows paint at rect.Y + index; selected rows carry Bold (Accent+Bold),
    // plain rows do not.
    private static bool IsPicked(ScreenBuffer buffer, int y) =>
        buffer.Get(0, y).Style.Attrs.HasFlag(StyleAttr.Bold);

    [Test]
    public async Task List_State_MirrorsItemCount()
    {
        var list = new DialogSelectList();
        list.SetItems(Options);
        await Assert.That(list.State.Count).IsEqualTo(Options.Length);
        await Assert.That(list.State.Selected).IsEqualTo(0);
        list.SetItems([]);
        await Assert.That(list.State.Count).IsEqualTo(0);
        await Assert.That(list.State.Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task List_ExternalState_DrivesHighlight()
    {
        var list = Open();
        list.State.MoveTo(2);

        var buffer = new ScreenBuffer(40, 8);
        list.Paint(buffer, new Rect(0, 0, 40, 8), list.State);

        await Assert.That(IsPicked(buffer, 0)).IsFalse();
        await Assert.That(IsPicked(buffer, 1)).IsFalse();
        await Assert.That(IsPicked(buffer, 2)).IsTrue();
        await Assert.That(IsPicked(buffer, 3)).IsFalse();
        await Assert.That(IsPicked(buffer, 4)).IsFalse();
    }

    [Test]
    public async Task List_SharedState_DrivesTwoLists()
    {
        // The #1158 contract: state is external, so one state object can drive
        // two lists and both paint the same selection.
        var left = Open();
        var right = Open();
        left.State.MoveTo(3);

        var buffer = new ScreenBuffer(40, 8);
        right.Paint(buffer, new Rect(0, 0, 40, 8), left.State);
        await Assert.That(IsPicked(buffer, 3)).IsTrue();
        await Assert.That(IsPicked(buffer, 0)).IsFalse();
    }

    [Test]
    public async Task List_ExternalState_ScrollsToSelection()
    {
        var list = OpenMany(20);
        list.State.SelectLast();

        // Height 2 → 2 list rows; selection drags the viewport down.
        var buffer = new ScreenBuffer(40, 2);
        list.Paint(buffer, new Rect(0, 0, 40, 2), list.State);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("Option 19")).IsTrue();
        await Assert.That(art.Contains("Option 00")).IsFalse();
    }

    [Test]
    public async Task List_MismatchedState_FallsBackToLegacy()
    {
        var list = Open();
        list.Move(1);
        var foreign = new GenericNavState();
        foreign.SetContent(99);
        foreign.MoveTo(50);

        var buffer = new ScreenBuffer(40, 8);
        list.Paint(buffer, new Rect(0, 0, 40, 8), foreign);
        await Assert.That(IsPicked(buffer, 1)).IsTrue();
        await Assert.That(IsPicked(buffer, 0)).IsFalse();
    }

    [Test]
    public async Task List_LegacyPaint_UnchangedByStatefulPaint()
    {
        var list = Open();
        list.State.MoveTo(4);

        // Stateful paint leaves the legacy selection alone: the legacy path
        // still highlights the first entry.
        var stateful = new ScreenBuffer(40, 8);
        list.Paint(stateful, new Rect(0, 0, 40, 8), list.State);
        await Assert.That(IsPicked(stateful, 4)).IsTrue();

        var buffer = new ScreenBuffer(40, 8);
        list.Paint(buffer, new Rect(0, 0, 40, 8));
        await Assert.That(IsPicked(buffer, 0)).IsTrue();
        await Assert.That(IsPicked(buffer, 1)).IsFalse();
        await Assert.That(list.SelectedIndex).IsEqualTo(0);
        await Assert.That(list.Viewport.Offset == 0).IsTrue();
    }
}
