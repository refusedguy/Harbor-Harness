using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Issue #2377: contract of the anchored popup slice. Geometry is resolved to
// a cell, the menu navigates a flat row projection with one submenu path, and
// DropdownButton composes the two with a primary action. This is deliberately
// NOT CommandPalettePrimitive (#2321): no fuzzy search, aliases or history.
public class AnchoredOverlayTests
{
    private static AnchoredMenu SimpleMenu() => new(
    [
        new MenuItem("open", "Open", "O"),
        new MenuItem("save", "Save", "S"),
    ]);

    private static AnchoredMenu TreeMenu(List<string> activated) => new(
    [
        new MenuItem("first", "First", "F"),
        new MenuItem("skip", "Disabled", "D", Disabled: true),
        new MenuItem("group", "Group", "G", Children:
        [
            new MenuItem("child", "Child", "C"),
        ]),
    ], activated.Add);

    [Test]
    public async Task Resolve_BelowFlipsAboveAndClampsToViewport()
    {
        var below = AnchoredOverlay.Resolve(
            new AnchorCell(5, 3), new OverlaySize(10, 4), new Rect(0, 0, 40, 24),
            AnchorSide.Bottom, 0, out var belowSide);
        await Assert.That(below).IsEqualTo(new Rect(5, 4, 10, 4));
        await Assert.That(belowSide).IsEqualTo(AnchorSide.Bottom);

        var flipped = AnchoredOverlay.Resolve(
            new AnchorCell(5, 7), new OverlaySize(10, 4), new Rect(0, 0, 40, 8),
            AnchorSide.Bottom, 0, out var flippedSide);
        await Assert.That(flipped).IsEqualTo(new Rect(5, 3, 10, 4));
        await Assert.That(flippedSide).IsEqualTo(AnchorSide.Top);

        var clamped = AnchoredOverlay.Resolve(
            new AnchorCell(5, 5), new OverlaySize(10, 4), new Rect(0, 0, 6, 6),
            AnchorSide.Bottom, 1, out var clampedSide);
        await Assert.That(clamped).IsEqualTo(new Rect(0, 0, 6, 4));
        await Assert.That(clampedSide).IsEqualTo(AnchorSide.Top);
    }

    [Test]
    public async Task Resolve_HorizontalSidesHonorGapAndNeverCoverAnchor()
    {
        var right = AnchoredOverlay.Resolve(
            new AnchorCell(5, 3), new OverlaySize(10, 4), new Rect(0, 0, 40, 24),
            AnchorSide.Right, 2, out var rightSide);
        await Assert.That(right).IsEqualTo(new Rect(8, 3, 10, 4));
        await Assert.That(rightSide).IsEqualTo(AnchorSide.Right);

        var left = AnchoredOverlay.Resolve(
            new AnchorCell(5, 3), new OverlaySize(10, 4), new Rect(0, 0, 12, 24),
            AnchorSide.Left, 0, out var leftSide);
        await Assert.That(left).IsEqualTo(new Rect(6, 3, 6, 4));
        await Assert.That(leftSide).IsEqualTo(AnchorSide.Right);

        var overlay = new AnchoredOverlay(
            new AnchorCell(5, 3), new OverlaySize(10, 4), new Rect(0, 0, 40, 24));
        await Assert.That(overlay.Flipped).IsFalse();
        overlay.Place(new AnchorCell(5, 7), new OverlaySize(10, 4), new Rect(0, 0, 40, 8));
        await Assert.That(overlay.Flipped).IsTrue();
        await Assert.That(overlay.Side).IsEqualTo(AnchorSide.Top);
    }

    [Test]
    public async Task FocusTrap_CyclesSlotsAndEscapeCloses()
    {
        var overlay = new AnchoredOverlay(
            new AnchorCell(1, 1), new OverlaySize(8, 3), new Rect(0, 0, 20, 10));
        overlay.SetFocusCount(3);
        overlay.Open();

        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(overlay.FocusIndex).IsEqualTo(1);
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Tab, KeyModifiers.Shift))).IsTrue();
        await Assert.That(overlay.FocusIndex).IsEqualTo(0);
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Tab, KeyModifiers.Shift))).IsTrue();
        await Assert.That(overlay.FocusIndex).IsEqualTo(2);

        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Tab, KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(overlay.FocusIndex).IsEqualTo(2);
        await Assert.That(overlay.HandleKey(new KeyEvent(
            KeyCode.Tab, default, KeyModifiers.None, KeyEventType.Release, false))).IsFalse();

        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task Menu_NavigationSkipsDisabledRowsAndWalksSubmenuPath()
    {
        var activated = new List<string>();
        var menu = TreeMenu(activated);
        menu.Open();

        await Assert.That(menu.Selected!.Value.Item.Id).IsEqualTo("first");
        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(menu.Selected!.Value.Item.Id).IsEqualTo("group");

        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();
        await Assert.That(menu.SubmenuDepth).IsEqualTo(1);
        await Assert.That(menu.Selected!.Value.Item.Id).IsEqualTo("child");

        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Left))).IsTrue();
        await Assert.That(menu.SubmenuDepth).IsEqualTo(0);
        await Assert.That(menu.Selected!.Value.Item.Id).IsEqualTo("group");

        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(menu.Selected!.Value.Item.Id).IsEqualTo("first");
        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(activated.Count).IsEqualTo(1);
        await Assert.That(activated[0]).IsEqualTo("first");
        await Assert.That(menu.IsOpen).IsFalse();
    }

    [Test]
    public async Task Menu_AltMnemonicFiresHotkeyButShiftAndChordsDoNot()
    {
        var activated = new List<string>();
        var menu = SimpleMenu();
        menu.SetItems([new MenuItem("save", "Save", "S")]);
        menu.Open();

        await Assert.That(menu.HandleKey(KeyEvent.Char(new Rune('S'), KeyModifiers.Shift))).IsFalse();
        await Assert.That(menu.HandleKey(KeyEvent.Char(new Rune('s'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(activated).IsEmpty();

        await Assert.That(menu.HandleKey(KeyEvent.Char(new Rune('s'), KeyModifiers.Alt))).IsTrue();
        await Assert.That(activated.Count).IsEqualTo(1);
        await Assert.That(activated[0]).IsEqualTo("save");
        await Assert.That(menu.IsOpen).IsFalse();
    }

    [Test]
    public async Task Menu_EscapeClosesOneSubmenuLevelAtATime()
    {
        var menu = TreeMenu([]);
        menu.Open();
        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();

        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(menu.SubmenuDepth).IsEqualTo(0);
        await Assert.That(menu.IsOpen).IsTrue();

        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(menu.IsOpen).IsFalse();
    }

    [Test]
    public async Task DropdownButton_RunsActionThenSelectsFromItsMenu()
    {
        int actions = 0;
        var selected = new List<string>();
        var button = new DropdownButton(
            "b", "Action", () => actions++,
            [new MenuItem("one", "One", "1"), new MenuItem("two", "Two", "2")],
            selected.Add);
        button.Place(new Rect(0, 0, 12, 1), new Rect(0, 0, 20, 8));

        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
        await Assert.That(actions).IsEqualTo(2);

        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(button.IsOpen).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(selected.Count).IsEqualTo(1);
        await Assert.That(selected[0]).IsEqualTo("one");
        await Assert.That(button.IsOpen).IsFalse();

        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(selected[1]).IsEqualTo("two");
    }

    [Test]
    public async Task DropdownButton_TabStaysInsideThePopupWhileOpen()
    {
        var button = new DropdownButton(
            "b", "Action", () => { }, [new MenuItem("one", "One", "1")]);
        button.Place(new Rect(0, 0, 12, 1), new Rect(0, 0, 20, 8));
        button.Open();

        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Tab, KeyModifiers.Shift))).IsTrue();
        await Assert.That(button.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(button.IsOpen).IsFalse();
    }
}
