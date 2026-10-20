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

    [Test]
    public async Task ShrinkToFit_Preserved_ByDefault_SC1212()
    {
        // SC4 #1212: the shared TableMeasurer must keep default paint
        // shrink-to-fit — desired [5,3] + decor 7 = 15 visible cells.
        var block = ParseSample();
        string first = ArtLines(block, 40)[0].TrimEnd();
        await Assert.That(first.Length).IsEqualTo(15);
        await Assert.That(first.StartsWith("┌")).IsTrue();
        await Assert.That(first.EndsWith("┐")).IsTrue();
    }

    [Test]
    public async Task ExpandToFill_StretchesRuleToWidth_SC1212()
    {
        var block = ParseSample();
        block.ExpandToFill = true;
        string first = ArtLines(block, 40)[0].TrimEnd();
        await Assert.That(first.Length).IsEqualTo(40);
        await Assert.That(first.StartsWith("┌")).IsTrue();
        await Assert.That(first.EndsWith("┐")).IsTrue();
    }

    [Test]
    public async Task Selection_DefaultsToNone()
    {
        var block = ParseSample();
        await Assert.That(block.SelectedRow.HasValue).IsFalse();
        await Assert.That(block.SelectedColumn.HasValue).IsFalse();
        await Assert.That(block.SelectedCell.HasValue).IsFalse();
        await Assert.That(block.HighlightSpacing).IsEqualTo(TableHighlightSpacing.WhenSelected);
        await Assert.That(block.ShouldHighlight).IsFalse();
        await Assert.That(block.BodyRows).IsEqualTo(2);
    }

    [Test]
    public async Task SelectRow_ClampsAndClears()
    {
        var block = ParseSample();
        block.SelectRow(99);
        await Assert.That(block.SelectedRow.Value).IsEqualTo(1);
        block.SelectRow(-5);
        await Assert.That(block.SelectedRow.Value).IsEqualTo(0);
        block.SelectRow(null);
        await Assert.That(block.SelectedRow.HasValue).IsFalse();
    }

    [Test]
    public async Task SelectColumn_ClampsAndClears()
    {
        var block = ParseSample(); // 2 columns
        block.SelectColumn(99);
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(1);
        block.SelectColumn(-5);
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(0);
        block.SelectColumn(null);
        await Assert.That(block.SelectedColumn.HasValue).IsFalse();
    }

    [Test]
    public async Task SelectCell_SetsBothAxes_AndNullClears()
    {
        var block = ParseSample();
        block.SelectCell((1, 0));
        await Assert.That(block.SelectedCell.Value).IsEqualTo((1, 0));
        await Assert.That(block.SelectedRow.Value).IsEqualTo(1);
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(0);
        block.SelectCell(null);
        await Assert.That(block.SelectedCell.HasValue).IsFalse();
        await Assert.That(block.SelectedRow.HasValue).IsFalse();
        await Assert.That(block.SelectedColumn.HasValue).IsFalse();
    }

    [Test]
    public async Task RowNavigation_NextPreviousFirstLast()
    {
        var block = ParseSample(); // 2 body rows
        block.SelectNext();
        await Assert.That(block.SelectedRow.Value).IsEqualTo(0);
        block.SelectNext();
        await Assert.That(block.SelectedRow.Value).IsEqualTo(1);
        block.SelectNext(); // saturates, no wrap
        await Assert.That(block.SelectedRow.Value).IsEqualTo(1);
        block.SelectPrevious();
        await Assert.That(block.SelectedRow.Value).IsEqualTo(0);
        block.SelectPrevious(); // saturates at first
        await Assert.That(block.SelectedRow.Value).IsEqualTo(0);

        var fresh = ParseSample();
        fresh.SelectPrevious(); // None → last
        await Assert.That(fresh.SelectedRow.Value).IsEqualTo(1);
        fresh.SelectFirst();
        await Assert.That(fresh.SelectedRow.Value).IsEqualTo(0);
        fresh.SelectLast();
        await Assert.That(fresh.SelectedRow.Value).IsEqualTo(1);
        fresh.ClearSelection();
        await Assert.That(fresh.SelectedRow.HasValue).IsFalse();
        await Assert.That(fresh.SelectedCell.HasValue).IsFalse();
    }

    [Test]
    public async Task ColumnNavigation_NextPreviousFirstLast()
    {
        var block = ParseSample(); // 2 columns
        block.SelectNextColumn();
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(0);
        block.SelectNextColumn();
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(1);
        block.SelectNextColumn(); // saturates, no wrap
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(1);

        var fresh = ParseSample();
        fresh.SelectPreviousColumn(); // None → last
        await Assert.That(fresh.SelectedColumn.Value).IsEqualTo(1);
        fresh.SelectFirstColumn();
        await Assert.That(fresh.SelectedColumn.Value).IsEqualTo(0);
        fresh.SelectLastColumn();
        await Assert.That(fresh.SelectedColumn.Value).IsEqualTo(1);
    }

    [Test]
    public async Task ScrollBy_SaturatesBothAxes()
    {
        var block = ParseSample();
        block.ScrollDownBy(99);
        await Assert.That(block.SelectedRow.Value).IsEqualTo(1);
        block.ScrollUpBy(99);
        await Assert.That(block.SelectedRow.Value).IsEqualTo(0);
        block.ScrollRightBy(99);
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(1);
        block.ScrollLeftBy(99);
        await Assert.That(block.SelectedColumn.Value).IsEqualTo(0);
        block.SelectRow(0);
        block.ScrollDownBy(1);
        await Assert.That(block.SelectedRow.Value).IsEqualTo(1);
    }

    [Test]
    public async Task HighlightSpacing_GatesPaint()
    {
        var block = ParseSample();
        block.SelectRow(0);
        await Assert.That(block.ShouldHighlight).IsTrue();
        var buffer = ScreenFixtures.PaintToBuffer(block);
        await Assert.That(buffer.Get(2, 3).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(2, 4).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();

        block.HighlightSpacing = TableHighlightSpacing.Never;
        await Assert.That(block.ShouldHighlight).IsFalse();
        var muted = ScreenFixtures.PaintToBuffer(block);
        await Assert.That(muted.Get(2, 3).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();

        var plain = ParseSample();
        var plainBuffer = ScreenFixtures.PaintToBuffer(plain);
        await Assert.That(plainBuffer.Get(2, 3).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
    }

    [Test]
    public async Task Paint_CellSelection_HighlightsSingleCell()
    {
        var block = ParseSample();
        block.SelectCell((1, 0)); // "grace" row, "name" column
        var buffer = ScreenFixtures.PaintToBuffer(block);
        await Assert.That(buffer.Get(2, 4).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(10, 4).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
        await Assert.That(buffer.Get(2, 3).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
    }

    [Test]
    public async Task Paint_ColumnSelection_HighlightsColumnIncludingHeader()
    {
        var block = ParseSample();
        block.SelectColumn(1); // "age" column
        var buffer = ScreenFixtures.PaintToBuffer(block);
        await Assert.That(buffer.Get(10, 1).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(10, 3).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsTrue();
        await Assert.That(buffer.Get(2, 1).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
        await Assert.That(buffer.Get(2, 3).Style.Attrs.HasFlag(StyleAttr.Reverse)).IsFalse();
    }

    [Test]
    public async Task RawText_Sample_ExactShape()
    {
        // #1097: pins the RawText shape (header, separator, body rows) that
        // the span rewrite must preserve byte-for-byte.
        string raw = ParseSample().RawText();
        await Assert.That(raw).IsEqualTo(
            "| name | age |\n| --- | ---: |\n| ada | 36 |\n| grace | 85 |");
    }

    [Test]
    public async Task RawText_EmptyBody_HeaderPlusSeparator()
    {
        // #1097: a header-only table stays two lines (no trailing newline).
        string[] lines = ["| only |", "| --- |"];
        bool ok = TableBlock.TryParse(lines, 0, out var block, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(block!.RawText()).IsEqualTo("| only |\n| --- |");
    }

    [Test]
    public async Task TableBlock_UsesNoHeapBuilder_RelativeGate()
    {
        // #1097 (relative gate, one site): TableBlock.cs must compose its
        // pipe text, rules and truncation without a heap builder — zero
        // allocations of that kind in this file. Span-based (join +
        // string.Create + prefix slice), not pooled, on purpose:
        // Harbor.Extensions is not on this assembly's matrix row, so
        // Presentation renders without the Application-layer pool. Scoped to
        // this file: OnboardingFlow, ... keep their own slices.
        string path = FindRepoFile(Path.Combine("src", "Harbor.Tui.CellForge", "Chat", "Widgets", "TableBlock.cs"));
        string source = await File.ReadAllTextAsync(path);
        await Assert.That(source.Contains("new StringBuilder")).IsFalse();
    }

    /// <summary>Locate a repo-relative source file by walking up from the test assembly directory.</summary>
    private static string FindRepoFile(string relativePath)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException($"Repo file not found: {relativePath}");
    }
}
