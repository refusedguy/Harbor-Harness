using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class TableBlockTests
{
    private static readonly string[] Sample =
    [
        "| name | age |",
        "| --- | ---: |",
        "| ada | 36 |",
        "| grace | 85 |",
    ];

    private static TableBlock ParseSample()
    {
        bool ok = TableBlock.TryParse(Sample, 0, out var block, out int next);
        if (!ok || block is null)
        {
            throw new InvalidOperationException("Sample must parse.");
        }

        if (next != Sample.Length)
        {
            throw new InvalidOperationException("Sample must consume all lines.");
        }

        return block;
    }

    private static string Art(TableBlock block, int width = 40)
    {
        var buffer = new ScreenBuffer(width, block.Measure(width).MinLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, width, block.Measure(width).MinLines), 0));
        return GridDump.Art(buffer);
    }

    private static string[] ArtLines(TableBlock block, int width = 40) =>
        Art(block, width).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task TryParse_ValidTable_ConsumesBlock()
    {
        bool ok = TableBlock.TryParse(Sample, 0, out var block, out int next);
        await Assert.That(ok).IsTrue();
        await Assert.That(block).IsNotNull();
        await Assert.That(next).IsEqualTo(4);
        await Assert.That(block!.Columns).IsEqualTo(2);
    }

    [Test]
    public async Task TryParse_NonTable_ReturnsFalse()
    {
        string[] lines = ["# hi", "plain text"];
        bool ok = TableBlock.TryParse(lines, 0, out var block, out int next);
        await Assert.That(ok).IsFalse();
        await Assert.That(block).IsNull();
        await Assert.That(next).IsEqualTo(0);
    }

    [Test]
    public async Task TryParse_StopsAtFirstNonRow()
    {
        string[] lines = [.. Sample, "", "trailing"];
        bool ok = TableBlock.TryParse(lines, 0, out var block, out int next);
        await Assert.That(ok).IsTrue();
        await Assert.That(next).IsEqualTo(4);
    }

    [Test]
    public async Task Measure_HeaderPlusRulesPlusRows()
    {
        var block = ParseSample(); // 2 body rows → 4 + 2
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(6);
        await Assert.That(block.Measure(40).IsExact).IsTrue();
        await Assert.That(block.CheapEstimate(40)).IsEqualTo(6);
    }

    [Test]
    public async Task Paint_RendersFrameHeaderAndCells()
    {
        string art = Art(ParseSample());
        await Assert.That(art).Contains("┌");
        await Assert.That(art).Contains("┐");
        await Assert.That(art).Contains("└");
        await Assert.That(art).Contains("┘");
        await Assert.That(art).Contains("name");
        await Assert.That(art).Contains("ada");
        await Assert.That(art).Contains("grace");
    }

    [Test]
    public async Task Paint_EveryRowSharesVisibleWidth()
    {
        var block = ParseSample();
        int width = 40;
        var buffer = new ScreenBuffer(width, block.Measure(width).MinLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, width, block.Measure(width).MinLines), 0));
        string art = GridDump.Art(buffer);
        foreach (string line in art.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            await Assert.That(line.Length).IsLessThanOrEqualTo(width);
        }
    }

    [Test]
    public async Task Paint_RightAlign_PadsLeft()
    {
        // "age" column is right-aligned: "36" must sit at the cell's right edge.
        string art = Art(ParseSample(), 20);
        string? row = art.Split('\n').FirstOrDefault(l => l.Contains("36"));
        await Assert.That(row).IsNotNull();
        int cell = row!.IndexOf("36", StringComparison.Ordinal);
        await Assert.That(row[cell - 1]).IsEqualTo(' ');
        await Assert.That(row[cell + 2]).IsEqualTo(' ');
    }

    [Test]
    public async Task Paint_NarrowWidth_TruncatesWithEllipsis()
    {
        var block = ParseSample();
        string art = Art(block, 12);
        await Assert.That(art).Contains("…");
        foreach (string line in art.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            await Assert.That(line.Length).IsLessThanOrEqualTo(12);
        }
    }

    [Test]
    public async Task Paint_SkipRows_PartialScroll()
    {
        var block = ParseSample();
        int height = block.Measure(40).MinLines;
        var buffer = new ScreenBuffer(40, height - 1);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, height - 1), 0, skipRows: 1));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("name");
        await Assert.That(art.Contains("┌")).IsFalse();
    }

    [Test]
    public async Task Paint_DegenerateRect_NoThrow()
    {
        var block = ParseSample();
        var buffer = new ScreenBuffer(40, 6);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 0, 6), 0));
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 0), 0));
        await Assert.That(block.RawText().Length).IsGreaterThan(0);
    }

    [Test]
    public async Task RawText_PipeFormWithSeparator()
    {
        string raw = ParseSample().RawText();
        await Assert.That(raw).Contains("| name | age |");
        await Assert.That(raw).Contains("| --- | ---: |");
        await Assert.That(raw).Contains("| ada | 36 |");
    }

    [Test]
    public async Task Contract_KindBudgetAndStreamFlag()
    {
        var block = ParseSample();
        await Assert.That(block.Kind).IsEqualTo("table");
        await Assert.That(block.IsStreamContinuation).IsFalse();
        await Assert.That(block.BudgetBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task EmptyBody_MeasuresFrameOnly()
    {
        string[] lines = ["| a | b |", "| --- | --- |"];
        bool ok = TableBlock.TryParse(lines, 0, out var block, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(block!.Measure(40).MinLines).IsEqualTo(4);
        string art = Art(block);
        await Assert.That(art).Contains("a");
    }

    [Test]
    public async Task Ctor_RejectsColumnlessTable()
    {
        var empty = new Harbor.Terminal.Abstractions.Rendering.GfmTable([], [], []);
        await Assert.That(() => new TableBlock(empty)).Throws<ArgumentException>();
    }
}
