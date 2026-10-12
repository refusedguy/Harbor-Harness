using Harbor.Terminal.Abstractions.Rendering;
using TUnit.Assertions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
/// Contract for <see cref="TableMeasurer" /> ([compose/spectre] #1212):
/// span-aware desired widths, greedy tallest-first collapse with NoWrap
/// last-resort, symmetric expand-distribute, and the full pipeline.
/// </summary>
public class TableMeasurerTests
{
    private static GfmTable Sample() => new(
        ["name", "age"],
        [["ada", "36"], ["grace", "85"]],
        [GfmAlign.Left, GfmAlign.Right]);

    [Test]
    public async Task GetNonColumnWidth_CountsBordersAndPadding()
    {
        await Assert.That(TableMeasurer.GetNonColumnWidth(2)).IsEqualTo(7);
        await Assert.That(TableMeasurer.GetNonColumnWidth(1)).IsEqualTo(4);
    }

    [Test]
    public async Task MeasureDesiredWidths_Sample_MaxPerColumn()
    {
        int[] widths = TableMeasurer.MeasureDesiredWidths(Sample());
        await Assert.That(widths).IsEquivalentTo([5, 3]);
    }

    [Test]
    public async Task MeasureDesiredWidths_FloorsAtMinCell()
    {
        var table = new GfmTable([""], [], [GfmAlign.Left]);
        await Assert.That(TableMeasurer.MeasureDesiredWidths(table)).IsEquivalentTo([TableMeasurer.MinCell]);
    }

    [Test]
    public async Task MeasureDesiredWidths_CapsAtMaxCell()
    {
        var table = new GfmTable([new string('x', 300)], [], [GfmAlign.Left]);
        await Assert.That(TableMeasurer.MeasureDesiredWidths(table)).IsEquivalentTo([TableMeasurer.MaxCell]);
    }

    [Test]
    public async Task MeasureDesiredWidths_BodySpan_SplitsAcrossColumns()
    {
        var table = new GfmTable(["x", "y"], [["abcdefghij", "q"]], [GfmAlign.Left, GfmAlign.Left]);
        await Assert.That(TableMeasurer.MeasureDesiredWidths(table)).IsEquivalentTo([10, 3]);

        int[] split = TableMeasurer.MeasureDesiredWidths(
            table, [new TableMeasurer.TableCellSpan(0, 0, 2)]);
        await Assert.That(split).IsEquivalentTo([5, 5]);
    }

    [Test]
    public async Task MeasureDesiredWidths_HeaderSpan_SplitsAcrossColumns()
    {
        var table = new GfmTable(["longheader", "b"], [], [GfmAlign.Left, GfmAlign.Left]);
        int[] split = TableMeasurer.MeasureDesiredWidths(
            table, [new TableMeasurer.TableCellSpan(-1, 0, 2)]);
        await Assert.That(split).IsEquivalentTo([5, 5]);
    }

    [Test]
    public async Task MeasureDesiredWidths_OutOfRangeSpan_Ignored()
    {
        int[] widths = TableMeasurer.MeasureDesiredWidths(
            Sample(), [new TableMeasurer.TableCellSpan(9, 9, 4)]);
        await Assert.That(widths).IsEquivalentTo([5, 3]);
    }

    [Test]
    public async Task CollapseWidths_TallestFirst()
    {
        int[] widths = [10, 6, 3];
        TableMeasurer.CollapseWidths(widths, 12);
        await Assert.That(widths).IsEquivalentTo([3, 6, 3]);
    }

    [Test]
    public async Task CollapseWidths_PinnedAtFloor_Stays()
    {
        int[] widths = [TableMeasurer.MinCell, TableMeasurer.MinCell];
        TableMeasurer.CollapseWidths(widths, 1);
        await Assert.That(widths).IsEquivalentTo([TableMeasurer.MinCell, TableMeasurer.MinCell]);
    }

    [Test]
    public async Task CollapseWidths_NoWrap_CollapsesLast()
    {
        int[] widths = [8, 8];
        TableMeasurer.CollapseWidths(widths, 10, noWrap: [true, false]);
        await Assert.That(widths).IsEquivalentTo([7, 3]);
    }

    [Test]
    public async Task DistributeWidths_FillsBudgetNarrowestFirst()
    {
        int[] widths = [5, 5];
        TableMeasurer.DistributeWidths(widths, 12);
        await Assert.That(widths).IsEquivalentTo([7, 5]);
    }

    [Test]
    public async Task DistributeWidths_RespectsMaxCell()
    {
        int[] widths = [TableMeasurer.MaxCell, 3];
        TableMeasurer.DistributeWidths(widths, 300);
        await Assert.That(widths).IsEquivalentTo([TableMeasurer.MaxCell, 44]);
    }

    [Test]
    public async Task DistributeWidths_AtOrOverBudget_NoOp()
    {
        int[] widths = [10, 10];
        TableMeasurer.DistributeWidths(widths, 20);
        await Assert.That(widths).IsEquivalentTo([10, 10]);
        TableMeasurer.DistributeWidths(widths, 5);
        await Assert.That(widths).IsEquivalentTo([10, 10]);
    }

    [Test]
    public async Task CalculateColumnWidths_Fit_KeepsDesiredByDefault()
    {
        int[] widths = TableMeasurer.CalculateColumnWidths(Sample(), 40);
        await Assert.That(widths).IsEquivalentTo([5, 3]);
    }

    [Test]
    public async Task CalculateColumnWidths_Overflow_CollapsesToBudget()
    {
        int[] widths = TableMeasurer.CalculateColumnWidths(Sample(), 10);
        await Assert.That(widths).IsEquivalentTo([3, 3]);
    }

    [Test]
    public async Task CalculateColumnWidths_ExpandToFill_DistributesSurplus()
    {
        int[] widths = TableMeasurer.CalculateColumnWidths(Sample(), 20, expandToFill: true);
        await Assert.That(widths).IsEquivalentTo([5, 8]);
    }

    [Test]
    public async Task InferNoWrap_SingleTokenColumns_Last()
    {
        var table = new GfmTable(
            ["first name", "age"],
            [["ada lovelace", "36"]],
            [GfmAlign.Left, GfmAlign.Right]);
        await Assert.That(TableMeasurer.InferNoWrap(table)).IsEquivalentTo([false, true]);
    }
}
