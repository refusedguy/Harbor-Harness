using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// [PRIM10] (#305) token-rate sparkline: ratatui sparkline port.
// Rendering reads global TerminalColorPalette — keep off the pty lane like TreeViewTests.
// #58: serialized vs theme tests.
[NotInParallel("pty")]
public class SparklineBlockTests
{
    [Test]
    public async Task Empty_MeasuresOne_PaintsPlaceholder()
    {
        var spark = new SparklineBlock();
        await Assert.That(spark.Values.Count).IsEqualTo(0);
        await Assert.That(spark.Measure(20).MinLines).IsEqualTo(1);
        await Assert.That(spark.Measure(20).IsExact).IsTrue();
        await Assert.That(spark.RawText()).IsEqualTo("(empty)");

        var buffer = new ScreenBuffer(20, 1);
        spark.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 20, 1), 0));
        await Assert.That(GridDump.Art(buffer)).Contains("(empty)");
    }

    [Test]
    public async Task Ascending_MapsLowToHighGlyphs()
    {
        string glyphs = SparklineBlock.MapToGlyphs([0, 1, 2, 3, 4, 5, 6, 7]);
        await Assert.That(glyphs).IsEqualTo(SparklineBlock.Levels);
    }

    [Test]
    public async Task Single_And_Flat_RenderMidGlyph()
    {
        await Assert.That(SparklineBlock.MapToGlyphs([42])).IsEqualTo("▄");
        await Assert.That(SparklineBlock.MapToGlyphs([3, 3, 3])).IsEqualTo("▄▄▄");
    }

    [Test]
    public async Task MaxOverride_ScalesDown()
    {
        // Half of an explicit max of 100 → middle of the range.
        string glyphs = SparklineBlock.MapToGlyphs([50], 100);
        await Assert.That(glyphs).IsEqualTo("▅");
    }

    [Test]
    public async Task Title_AddsRow_PaintsDim()
    {
        var spark = new SparklineBlock([1, 2, 3], title: "tok/s");
        await Assert.That(spark.Measure(20).MinLines).IsEqualTo(2);
        await Assert.That(spark.CheapEstimate(20)).IsEqualTo(2);
        await Assert.That(spark.RawText()).IsEqualTo("tok/s\n" + SparklineBlock.MapToGlyphs([1, 2, 3]));

        var buffer = new ScreenBuffer(20, 2);
        spark.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 20, 2), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("tok/s");
        await Assert.That(art).Contains(SparklineBlock.MapToGlyphs([1, 2, 3]));
    }

    [Test]
    public async Task Paint_ClipsToWidth()
    {
        var spark = new SparklineBlock([0, 1, 2, 3, 4, 5, 6, 7]);
        var buffer = new ScreenBuffer(4, 1);
        spark.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 4, 1), 0));
        await Assert.That(GridDump.Art(buffer)).Contains(SparklineBlock.Levels[..4]);
    }

    [Test]
    public async Task NegativeAndNaN_SanitizedToZero()
    {
        string glyphs = SparklineBlock.MapToGlyphs([double.NaN, -5, 10]);
        // NaN/-5 → 0, so both sit at the bottom with the min.
        await Assert.That(glyphs[0]).IsEqualTo(glyphs[1]);
        await Assert.That(glyphs[2]).IsEqualTo('█');
    }

    [Test]
    public async Task Contract_KindBudgetAndStreamFlag()
    {
        var spark = new SparklineBlock([1, 2], title: "t");
        await Assert.That(spark.Kind).IsEqualTo("sparkline");
        await Assert.That(spark.IsStreamContinuation).IsFalse();
        await Assert.That(spark.BudgetBytes).IsGreaterThan(0);
        await Assert.That(spark.CheapEstimate(20)).IsGreaterThan(0);
    }
}
