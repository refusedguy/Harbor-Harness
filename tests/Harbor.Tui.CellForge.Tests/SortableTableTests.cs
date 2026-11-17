using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class SortableTableTests
{
    private static SortableTable TextTable()
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("name"), new TableColumn("group")]);
        table.AddRow([TableCell.Of("delta"), TableCell.Of("b")]);
        table.AddRow([TableCell.Of("alpha"), TableCell.Of("a")]);
        table.AddRow([TableCell.Of("charlie"), TableCell.Of("b")]);
        table.AddRow([TableCell.Of("bravo"), TableCell.Of("a")]);
        return table;
    }

    private static SortableTable NumberedTable(int count)
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("id")]);
        for (int i = 0; i < count; i++)
        {
            table.AddRow([TableCell.Of("r" + i)]);
        }

        return table;
    }

    private static string Art(SortableTable table, int width, int height, int offset, int selected = -1)
    {
        var buffer = TestRender.Buffer(width, height);
        table.Paint(buffer, new Rect(0, 0, width, height), offset, selected);
        return ScreenFixtures.Art(buffer);
    }

    // ---- sorting ---------------------------------------------------------

    [Test]
    public async Task Sort_AscendingByTextColumn_UsesOrdinalOrder()
    {
        var table = TextTable();
        table.SortBy(0);

        await Assert.That(table.Sort?.Column).IsEqualTo(0);
        await Assert.That(table.Sort?.Direction).IsEqualTo(SortDirection.Ascending);
        // source indices: alpha(1), bravo(3), charlie(2), delta(0)
        await Assert.That(table.Order).IsEquivalentTo([1, 3, 2, 0]);
        await Assert.That(table.Rows[0][0].Text).IsEqualTo("delta"); // source order untouched
    }

    [Test]
    public async Task Sort_DescendingByTextColumn_ReversesComparison()
    {
        var table = TextTable();
        table.SortBy(0, SortDirection.Descending);

        await Assert.That(table.Order).IsEquivalentTo([0, 2, 3, 1]);
    }

    [Test]
    public async Task Sort_StableTieBreak_KeepsSourceOrder()
    {
        var table = TextTable();
        // group "a": rows 1 and 3; group "b": rows 0 and 2 — ties keep source order.
        table.SortBy(1);

        await Assert.That(table.Order).IsEquivalentTo([1, 3, 0, 2]);
    }

    [Test]
    public async Task Sort_GaugeColumn_UsesRatioNotLabel()
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("load", TableColumnKind.Gauge)]);
        table.AddRow([TableCell.Gauge(0.9, "90% high")]);
        table.AddRow([TableCell.Gauge(0.1, "10% low")]);
        table.AddRow([TableCell.Gauge(0.5, "50% mid")]);

        table.SortBy(0);
        await Assert.That(table.Order).IsEquivalentTo([1, 2, 0]);

        table.SortBy(0, SortDirection.Descending);
        await Assert.That(table.Order).IsEquivalentTo([0, 2, 1]);
    }

    [Test]
    public async Task ToggleSort_CyclesAscendingThenDescending()
    {
        var table = TextTable();
        table.ToggleSort(0);
        await Assert.That(table.Sort?.Direction).IsEqualTo(SortDirection.Ascending);
        table.ToggleSort(0);
        await Assert.That(table.Sort?.Direction).IsEqualTo(SortDirection.Descending);
        table.ToggleSort(0);
        await Assert.That(table.Sort?.Direction).IsEqualTo(SortDirection.Ascending);

        table.ToggleSort(1);
        await Assert.That(table.Sort?.Column).IsEqualTo(1);
        await Assert.That(table.Sort?.Direction).IsEqualTo(SortDirection.Ascending);
    }

    [Test]
    public async Task ClearSort_RestoresSourceOrder()
    {
        var table = TextTable();
        table.SortBy(0, SortDirection.Descending);
        table.ClearSort();

        await Assert.That(table.Sort).IsNull();
        await Assert.That(table.Order).IsEquivalentTo([0, 1, 2, 3]);
    }

    [Test]
    public async Task SortBy_ClampsColumnIndex()
    {
        var table = TextTable();
        table.SortBy(99);
        await Assert.That(table.Sort?.Column).IsEqualTo(1);
        table.SortBy(-5);
        await Assert.That(table.Sort?.Column).IsEqualTo(0);
    }

    // ---- sticky header + virtualization ----------------------------------

    [Test]
    public async Task Header_IsStickyAcrossScroll()
    {
        var table = NumberedTable(10);

        string top = Art(table, 12, 6, offset: 0);
        string scrolled = Art(table, 12, 6, offset: 6);

        // GenericTable frame: top rule at row 0, sticky header at row 1.
        string topHeader = top.Split('\n')[1];
        string scrolledHeader = scrolled.Split('\n')[1];
        await Assert.That(topHeader).IsEqualTo(scrolledHeader);
        await Assert.That(topHeader).Contains("id");
        // body content differs, so the two frames are not just identical
        await Assert.That(top).IsNotEqualTo(scrolled);
    }

    [Test]
    public async Task VisibleRows_ClampsToDisplayOrderSlice()
    {
        var table = NumberedTable(10);

        await Assert.That(table.BodyCapacity(6)).IsEqualTo(2);
        await Assert.That(table.VisibleRows(2, 0)).IsEqualTo((0, 2));
        await Assert.That(table.VisibleRows(2, 5)).IsEqualTo((5, 7));
        await Assert.That(table.VisibleRows(2, 99)).IsEqualTo((8, 10)); // clamped to last page
        await Assert.That(table.VisibleRows(2, -3)).IsEqualTo((0, 2));
        await Assert.That(table.VisibleRows(0, 0)).IsEqualTo((0, 0));
    }

    [Test]
    public async Task Virtualization_OnlyPaintsVisibleSlice()
    {
        var table = NumberedTable(10);

        string art = Art(table, 12, 6, offset: 5);

        await Assert.That(art).Contains("r5");
        await Assert.That(art).Contains("r6");
        await Assert.That(art).DoesNotContain("r0");
        await Assert.That(art).DoesNotContain("r4");
        await Assert.That(art).DoesNotContain("r7");
    }

    [Test]
    public async Task Virtualization_FollowsSortedOrder()
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("name")]);
        table.AddRow([TableCell.Of("delta")]);
        table.AddRow([TableCell.Of("alpha")]);
        table.AddRow([TableCell.Of("charlie")]);
        table.AddRow([TableCell.Of("bravo")]);
        table.SortBy(0); // display order: alpha, bravo, charlie, delta

        string art = Art(table, 14, 6, offset: 2); // capacity 2 → rows 2,3

        await Assert.That(art).Contains("charlie");
        await Assert.That(art).Contains("delta");
        await Assert.That(art).DoesNotContain("alpha");
        await Assert.That(art).DoesNotContain("bravo");
    }

    [Test]
    public async Task NavState_DrivesScrollOffset()
    {
        var table = NumberedTable(10);
        var state = new GenericNavState();
        state.SetContent(table.Rows.Count, viewportH: 2);
        state.SelectLast(); // selection 9 drags the viewport to offset 8
        state.ScrollToEnd();

        var buffer = TestRender.Buffer(12, 6);
        table.Paint(buffer, new Rect(0, 0, 12, 6), state);
        string art = ScreenFixtures.Art(buffer);

        await Assert.That(art).Contains("r8");
        await Assert.That(art).Contains("r9");
        await Assert.That(art).DoesNotContain("r0");
    }

    // ---- gauge + panel-row bridge ---------------------------------------

    [Test]
    public async Task GaugeColumn_PaintsGaugeBar()
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("load", TableColumnKind.Gauge)]);
        table.AddRow([TableCell.Gauge(1.0)]);
        table.AddRow([TableCell.Gauge(0.0)]);

        string art = Art(table, 16, 6, offset: 0); // capacity 2 → both rows

        // Single "load" column fits to 4 content cells in a 16-wide buffer.
        await Assert.That(art).Contains(new string(GaugeBar.Filled, 4));
        await Assert.That(art).Contains(new string(GaugeBar.Track, 4));
    }

    [Test]
    public async Task SetRowsFromLines_SplitsPanelRowsIntoCells()
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("pid"), new TableColumn("cmd")]);
        // The plain shape produced by PanelRows.*Rows builders.
        table.SetRowsFromLines(["100|dotnet", "200|code"]);

        await Assert.That(table.Rows.Count).IsEqualTo(2);
        await Assert.That(table.Rows[0][0].Text).IsEqualTo("100");
        await Assert.That(table.Rows[0][1].Text).IsEqualTo("dotnet");

        table.SortBy(0, SortDirection.Descending);
        await Assert.That(table.Order).IsEquivalentTo([1, 0]);
    }

    [Test]
    public async Task SetRowsFromLines_CollapsesEmbeddedNewlines()
    {
        var table = new SortableTable();
        table.SetColumns([new TableColumn("level"), new TableColumn("message")]);
        table.SetRowsFromLines(["INFO|first\r\nsecond"]);

        await Assert.That(table.Rows.Count).IsEqualTo(1);
        // SingleLine substitutes each CR and LF with a space: "first" + "  " + "second".
        await Assert.That(table.Rows[0][1].Text).IsEqualTo("first  second");
    }

    [Test]
    public async Task SetColumns_And_ClearRows_KeepInvariants()
    {
        var table = TextTable();
        table.SortBy(0);
        table.ClearRows();
        await Assert.That(table.Rows.Count).IsEqualTo(0);
        await Assert.That(table.Order).IsEmpty();
        await Assert.That(table.VisibleRows(2, 0)).IsEqualTo((0, 0));

        table.AddRow([TableCell.Of("x")]);
        await Assert.That(table.Order).IsEquivalentTo([0]);
    }
}
