using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Issue #2377: pinned grid art for the anchored overlay slice. The snapshots
// are inline on purpose: this slice adds no new fixture suite, and the rows
// below are exactly what a user reads in the terminal. Painting reads the
// global palette, so these serialize with the other paint tests.
[NotInParallel("pty")]
public class AnchoredOverlayGoldenTests
{
    private static string Art(ScreenBuffer buffer, int firstRow, int lastRow, int cols)
    {
        var lines = new string[lastRow - firstRow + 1];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = ScreenFixtures.Row(buffer, firstRow + i, cols).TrimEnd();
        }

        return string.Join("\n", lines);
    }

    private static AnchoredMenu SaveMenu() => new(
    [
        new MenuItem("open", "Open", "O"),
        new MenuItem("save", "Save", "S"),
    ]);

    [Test]
    public async Task Golden_MenuBelowAnchor()
    {
        var menu = SaveMenu();
        menu.Open();
        var overlay = new AnchoredOverlay(
            new AnchorCell(3, 2), menu.ContentSize, new Rect(0, 0, 20, 8));
        overlay.Open();

        var buffer = TestRender.Buffer(20, 8);
        menu.Paint(buffer, overlay.Bounds);

        await Assert.That(Art(buffer, 3, 6, 20)).IsEqualTo(
            "+--------+\n"
            + "|  Open O|\n"
            + "|  Save S|\n"
            + "+--------+");
    }

    [Test]
    public async Task Golden_MenuFlipsAboveAnchor()
    {
        var menu = SaveMenu();
        menu.Open();
        var overlay = new AnchoredOverlay(
            new AnchorCell(3, 5), menu.ContentSize, new Rect(0, 0, 20, 6));
        overlay.Open();

        var buffer = TestRender.Buffer(20, 6);
        menu.Paint(buffer, overlay.Bounds);

        await Assert.That(overlay.Side).IsEqualTo(AnchorSide.Top);
        await Assert.That(Art(buffer, 1, 4, 20)).IsEqualTo(
            "+--------+\n"
            + "|  Open O|\n"
            + "|  Save S|\n"
            + "+--------+");
    }

    [Test]
    public async Task Golden_ExpandedSubmenuShowsNestedRows()
    {
        var menu = new AnchoredMenu(
        [
            new MenuItem("file", "File", "F", Children:
            [
                new MenuItem("new", "New", "N"),
                new MenuItem("exit", "Exit", "X"),
            ]),
            new MenuItem("help", "Help", "H"),
        ]);
        menu.Open();
        await Assert.That(menu.HandleKey(KeyEvent.Simple(KeyCode.Right))).IsTrue();

        var overlay = new AnchoredOverlay(
            new AnchorCell(2, 0), menu.ContentSize, new Rect(0, 0, 20, 10));
        overlay.Open();

        var buffer = TestRender.Buffer(20, 10);
        menu.Paint(buffer, overlay.Bounds);

        await Assert.That(Art(buffer, 1, 6, 20)).IsEqualTo(
            "+----------+\n"
            + "|  File > F|\n"
            + "|    New  N|\n"
            + "|    Exit X|\n"
            + "|  Help   H|\n"
            + "+----------+");
    }

    [Test]
    public async Task Golden_DropdownButtonPaintsActionAndAnchoredMenu()
    {
        var button = new DropdownButton(
            "b", "Action", () => { },
            [new MenuItem("one", "One", "1"), new MenuItem("two", "Two", "2")]);
        button.Place(new Rect(0, 0, 12, 1), new Rect(0, 0, 20, 8));
        button.Open();

        var buffer = TestRender.Buffer(20, 8);
        button.Paint(buffer);

        await Assert.That(Art(buffer, 0, 4, 20)).IsEqualTo(
            "[ Action \u25be ]\n"
            + "+--------+\n"
            + "|  One 1|\n"
            + "|  Two 2|\n"
            + "+--------+");
    }

    [Test]
    public async Task Paint_SelectedRowHasItsOwnStyle()
    {
        var menu = SaveMenu();
        menu.Open();
        var buffer = TestRender.Buffer(20, 8);
        menu.Paint(buffer, new Rect(0, 0, 10, 4));

        var selected = buffer.Get(1, 1);
        var plain = buffer.Get(1, 2);
        await Assert.That(selected.HasSameStyle(plain)).IsFalse();
    }
}
