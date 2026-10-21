using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class ColumnsBlockTests
{
    private static string Art(ColumnsBlock block, int width)
    {
        var measure = block.Measure(width);
        var buffer = TestRender.Buffer(width, Math.Max(1, measure.MinLines));
        block.Paint(TestRender.Ctx(buffer));
        return TestRender.Art(buffer);
    }

    private static string[] ArtLines(ColumnsBlock block, int width) =>
        Art(block, width).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task WideWidth_FitsSingleRow()
    {
        var block = new ColumnsBlock(["aa", "bb", "cc", "dd"]);
        await Assert.That(block.LayoutColumns(40)).IsEqualTo(4);
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(1);
        await Assert.That(block.Measure(40).IsExact).IsTrue();
    }

    [Test]
    public async Task NarrowWidth_ShrinksColumnCount()
    {
        // 4 items of width 10, gap 2: 4 cols need 46 > 40, 3 cols need 34.
        var block = new ColumnsBlock(["0123456789", "abcdefghij", "ABCDEFGHIJ", "----------"]);
        await Assert.That(block.LayoutColumns(40)).IsEqualTo(3);
        await Assert.That(block.RowCount(40)).IsEqualTo(2);
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(2);
    }

    [Test]
    public async Task SingleOverWideItem_OneColumnAndTruncates()
    {
        var block = new ColumnsBlock(["0123456789abcdef"]);
        await Assert.That(block.LayoutColumns(8)).IsEqualTo(1);
        string[] lines = ArtLines(block, 8);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("0123456…");
    }

    [Test]
    public async Task Paint_FillsRowsLeftToRight()
    {
        // 3 cols need 3*2 + 2*2 = 10 cells ≤ 12; 4 cols need 22 > 12.
        var block = new ColumnsBlock(["aa", "bb", "cc", "dd", "ee"], gap: 2);
        await Assert.That(block.LayoutColumns(12)).IsEqualTo(3);
        string[] lines = ArtLines(block, 12);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0].TrimEnd()).IsEqualTo("aa  bb  cc");
        await Assert.That(lines[1].TrimEnd()).IsEqualTo("dd  ee");
    }

    [Test]
    public async Task Paint_WideWidth_SingleRowNoTrailingGap()
    {
        var block = new ColumnsBlock(["aa", "bb", "cc", "dd"]);
        string[] lines = ArtLines(block, 40);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0].TrimEnd()).IsEqualTo("aa  bb  cc  dd");
    }

    [Test]
    public async Task Empty_NoRowsAndEmptyRawText()
    {
        var block = new ColumnsBlock([]);
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(0);
        await Assert.That(block.CheapEstimate(40)).IsEqualTo(0);
        await Assert.That(block.RawText()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CheapEstimate_SingleColumnBound()
    {
        var block = new ColumnsBlock(["a", "b", "c"]);
        await Assert.That(block.CheapEstimate(80)).IsEqualTo(3);
    }

    [Test]
    public async Task RawText_GapJoined()
    {
        var block = new ColumnsBlock(["a", "b", "c"]);
        await Assert.That(block.RawText()).IsEqualTo("a  b  c");
    }

    [Test]
    public async Task Guards_RejectBadArgs()
    {
        await Assert.That(() => new ColumnsBlock(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new ColumnsBlock(["a"], gap: -1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CalculateColumnCount_MatchesSpectreFitLoop()
    {
        // 4 × 10-wide items, gap 2, width 40: 4 cols need 46, 3 cols need 34.
        await Assert.That(ColumnsBlock.CalculateColumnCount(40, [10, 10, 10, 10], 4, 2)).IsEqualTo(3);
        // Degenerate width: Spectre returns 0 (caller falls back to 1 column).
        await Assert.That(ColumnsBlock.CalculateColumnCount(5, [10, 10], 2, 2)).IsEqualTo(0);
    }
}
