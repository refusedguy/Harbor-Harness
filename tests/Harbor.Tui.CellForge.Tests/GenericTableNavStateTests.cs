using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GenericTableNavStateTests
{
    private static GenericTable Open()
    {
        var table = new GenericTable();
        table.SetHeaders(["name", "status"]);
        table.AddRow(["r0", "ok"]);
        table.AddRow(["r1", "ok"]);
        table.AddRow(["r2", "ok"]);
        return table;
    }

    private static bool IsReversed(ScreenBuffer buffer, int x, int y) =>
        buffer.Get(x, y).Style.Attrs.HasFlag(StyleAttr.Reverse);

    [Test]
    public async Task Table_State_MirrorsRowCount()
    {
        var table = new GenericTable();
        table.SetHeaders(["h"]);
        await Assert.That(table.State.Count).IsEqualTo(0);
        await Assert.That(table.State.Selected).IsEqualTo(-1);
        table.AddRow(["a"]);
        table.AddRow(["b"]);
        await Assert.That(table.State.Count).IsEqualTo(2);
        await Assert.That(table.State.Selected).IsEqualTo(0);
        table.ClearRows();
        await Assert.That(table.State.Count).IsEqualTo(0);
        await Assert.That(table.State.Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task Table_ExternalState_DrivesHighlight()
    {
        var table = Open();
        table.State.MoveTo(1);
        var buffer = new ScreenBuffer(30, 8);
        table.Paint(buffer, new Rect(0, 0, 30, 8), table.State);

        // Body rows start at y=3 (top rule, header, mid rule); text at x=2.
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
        await Assert.That(IsReversed(buffer, 2, 4)).IsTrue();
        await Assert.That(IsReversed(buffer, 2, 5)).IsFalse();
    }

    [Test]
    public async Task Table_SharedState_DrivesTwoTables()
    {
        // The #1158 contract: state is external, so one state object can drive
        // two tables and both paint the same selection.
        var left = Open();
        var right = Open();
        left.State.MoveTo(2);

        var buffer = new ScreenBuffer(30, 8);
        right.Paint(buffer, new Rect(0, 0, 30, 8), left.State);
        await Assert.That(IsReversed(buffer, 2, 5)).IsTrue();
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
    }

    [Test]
    public async Task Table_ExternalState_ScrollsToSelection()
    {
        var table = new GenericTable();
        table.SetHeaders(["name"]);
        table.AddRow(["r0"]);
        table.AddRow(["r1"]);
        table.AddRow(["r2"]);
        table.AddRow(["r3"]);
        table.AddRow(["r4"]);
        table.State.SelectLast();

        // Height 6 → frame 4 + 2 body rows; selection drags the viewport down.
        var buffer = new ScreenBuffer(20, 6);
        table.Paint(buffer, new Rect(0, 0, 20, 6), table.State);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("r4")).IsTrue();
        await Assert.That(art.Contains("r0")).IsFalse();
    }

    [Test]
    public async Task Table_MismatchedState_FallsBackToSelectedRow()
    {
        var table = Open();
        table.SelectedRow = 1;
        var foreign = new GenericNavState();
        foreign.SetContent(99);
        foreign.MoveTo(50);

        var buffer = new ScreenBuffer(30, 8);
        table.Paint(buffer, new Rect(0, 0, 30, 8), foreign);
        await Assert.That(IsReversed(buffer, 2, 4)).IsTrue();
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
    }

    [Test]
    public async Task Table_LegacyPaint_NoSelectionByDefault()
    {
        var table = Open();
        var buffer = new ScreenBuffer(30, 8);
        table.Paint(buffer, new Rect(0, 0, 30, 8));
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
        await Assert.That(IsReversed(buffer, 2, 4)).IsFalse();
        await Assert.That(IsReversed(buffer, 2, 5)).IsFalse();

        table.SelectedRow = 0;
        var selected = new ScreenBuffer(30, 8);
        table.Paint(selected, new Rect(0, 0, 30, 8));
        await Assert.That(IsReversed(selected, 2, 3)).IsTrue();
        await Assert.That(IsReversed(selected, 2, 4)).IsFalse();
    }
}
