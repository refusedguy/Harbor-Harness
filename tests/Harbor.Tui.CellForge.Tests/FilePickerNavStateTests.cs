using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class FilePickerNavStateTests
{
    private static readonly FilePickerItem[] Files =
    [
        new("src/a.cs", "first"),
        new("src/b.cs", "second"),
        new("src/c.cs", "third"),
        new("src/d.cs", "fourth"),
        new("src/e.cs", "fifth"),
    ];

    private static FilePickerView Open()
    {
        var picker = new FilePickerView();
        picker.Show(Files);
        return picker;
    }

    // List rows start at rect.Y + 2; the entry text paints at rect.X + 1.
    // Selected rows carry Bold (Accent+Bold), plain rows do not (query row
    // at y=1 is Bold too, so only list rows are graded).
    private static bool IsPicked(ScreenBuffer buffer, int y) =>
        buffer.Get(1, y).Style.Attrs.HasFlag(StyleAttr.Bold);

    [Test]
    public async Task Picker_State_MirrorsResultCount()
    {
        var picker = new FilePickerView();
        picker.Show(Files);
        await Assert.That(picker.State.Count).IsEqualTo(Files.Length);
        await Assert.That(picker.State.Selected).IsEqualTo(0);
        picker.Hide();
        await Assert.That(picker.State.Count).IsEqualTo(0);
        await Assert.That(picker.State.Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task Picker_ExternalState_DrivesHighlight()
    {
        var picker = Open();
        picker.State.MoveTo(2);

        var buffer = TestRender.Buffer(60, 12);
        picker.Paint(buffer, new Rect(0, 0, 60, 12), picker.State);

        await Assert.That(IsPicked(buffer, 2)).IsFalse();
        await Assert.That(IsPicked(buffer, 3)).IsFalse();
        await Assert.That(IsPicked(buffer, 4)).IsTrue();
        await Assert.That(IsPicked(buffer, 5)).IsFalse();
    }

    [Test]
    public async Task Picker_SharedState_DrivesTwoPickers()
    {
        // The #1158 contract: state is external, so one state object can drive
        // two pickers and both paint the same selection.
        var left = Open();
        var right = Open();
        left.State.MoveTo(3);

        var buffer = TestRender.Buffer(60, 12);
        right.Paint(buffer, new Rect(0, 0, 60, 12), left.State);
        await Assert.That(IsPicked(buffer, 5)).IsTrue();
        await Assert.That(IsPicked(buffer, 2)).IsFalse();
    }

    [Test]
    public async Task Picker_ExternalState_ScrollsToSelection()
    {
        var picker = Open();
        picker.State.SelectLast();

        // Height 6 → 2 list rows; selection drags the viewport down.
        var buffer = TestRender.Buffer(60, 6);
        picker.Paint(buffer, new Rect(0, 0, 60, 6), picker.State);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("src/e.cs")).IsTrue();
        await Assert.That(art.Contains("src/a.cs")).IsFalse();
    }

    [Test]
    public async Task Picker_MismatchedState_FallsBackToLegacy()
    {
        var picker = Open();
        _ = picker.HandleKey(KeyEvent.Simple(KeyCode.Down));
        var foreign = new GenericNavState();
        foreign.SetContent(99);
        foreign.MoveTo(50);

        var buffer = TestRender.Buffer(60, 12);
        picker.Paint(buffer, new Rect(0, 0, 60, 12), foreign);
        await Assert.That(IsPicked(buffer, 3)).IsTrue();
        await Assert.That(IsPicked(buffer, 2)).IsFalse();
    }

    [Test]
    public async Task Picker_LegacyPaint_UnchangedByStatefulPaint()
    {
        var picker = Open();
        picker.State.MoveTo(4);

        // Stateful paint leaves the legacy selection alone: the legacy path
        // still highlights the first entry.
        var stateful = TestRender.Buffer(60, 12);
        picker.Paint(stateful, new Rect(0, 0, 60, 12), picker.State);
        await Assert.That(IsPicked(stateful, 6)).IsTrue();

        var buffer = TestRender.Buffer(60, 12);
        picker.Paint(buffer, new Rect(0, 0, 60, 12));
        await Assert.That(IsPicked(buffer, 2)).IsTrue();
        await Assert.That(IsPicked(buffer, 3)).IsFalse();
        await Assert.That(picker.SelectedIndex).IsEqualTo(0);
    }
}
