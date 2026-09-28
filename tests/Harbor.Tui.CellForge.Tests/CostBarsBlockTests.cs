using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// [PRIM10] (#305) cost-bar chart: ratatui chart / bubbles-progress port.
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class CostBarsBlockTests
{
    [Test]
    public async Task Empty_MeasuresOne_PaintsPlaceholder()
    {
        var bars = new CostBarsBlock();
        await Assert.That(bars.Bars.Count).IsEqualTo(0);
        await Assert.That(bars.Measure(30).MinLines).IsEqualTo(1);
        await Assert.That(bars.Measure(30).IsExact).IsTrue();
        await Assert.That(bars.RawText()).IsEqualTo("(empty)");

        var buffer = new ScreenBuffer(30, 1);
        bars.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 30, 1), 0));
        await Assert.That(GridDump.Art(buffer)).Contains("(empty)");
    }

    [Test]
    public async Task SingleBar_FillsFullTrack()
    {
        var bars = new CostBarsBlock([new CostBar("sonnet", 1.5)]);
        await Assert.That(bars.MaxValue).IsEqualTo(1.5);
        await Assert.That(bars.Measure(30).MinLines).IsEqualTo(1);

        string row = CostBarsBlock.RenderRow("sonnet", 1.5, 1.5, 30);
        await Assert.That(row).Contains("sonnet");
        await Assert.That(row).Contains("1.5");
        await Assert.That(row.Contains('░')).IsFalse(); // maxed out: no empty cells
    }

    [Test]
    public async Task Scaling_ProportionalToMax()
    {
        string full = CostBarsBlock.RenderRow("a", 4, 4, 20);
        string half = CostBarsBlock.RenderRow("b", 2, 4, 20);
        int fullFill = Count(full, '█');
        int halfFill = Count(half, '█');
        await Assert.That(fullFill).IsGreaterThan(0);
        await Assert.That(halfFill).IsEqualTo(fullFill / 2);
    }

    [Test]
    public async Task ZeroMax_RendersEmptyTrack()
    {
        string row = CostBarsBlock.RenderRow("a", 0, 0, 20);
        await Assert.That(row.Contains('█')).IsFalse();
        await Assert.That(row.Contains('░')).IsTrue();
    }

    [Test]
    public async Task NegativeAndNaN_Sanitized()
    {
        var bars = new CostBarsBlock([new CostBar("x", -2), new CostBar("y", double.NaN)]);
        await Assert.That(bars.Bars[0].Value).IsEqualTo(0);
        await Assert.That(bars.Bars[1].Value).IsEqualTo(0);
        await Assert.That(bars.MaxValue).IsEqualTo(0);
    }

    [Test]
    public async Task Title_AddsRow_PaintsAllBars()
    {
        var bars = new CostBarsBlock(
            [new CostBar("sonnet", 1.5), new CostBar("haiku", 0.5)],
            title: "cost $");
        await Assert.That(bars.Measure(30).MinLines).IsEqualTo(3);
        await Assert.That(bars.RawText()).IsEqualTo("cost $\nsonnet 1.5\nhaiku 0.5");

        var buffer = new ScreenBuffer(30, 3);
        bars.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 30, 3), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("cost $");
        await Assert.That(art).Contains("sonnet");
        await Assert.That(art).Contains("haiku");
    }

    [Test]
    public async Task NarrowWidth_ClipsLabel()
    {
        string row = CostBarsBlock.RenderRow("very-long-model-name", 1, 2, 6);
        await Assert.That(row.Length).IsEqualTo(6);
    }

    [Test]
    public async Task Contract_KindBudgetAndStreamFlag()
    {
        var bars = new CostBarsBlock([new CostBar("a", 1)]);
        await Assert.That(bars.Kind).IsEqualTo("cost-bars");
        await Assert.That(bars.IsStreamContinuation).IsFalse();
        await Assert.That(bars.BudgetBytes).IsGreaterThan(0);
        await Assert.That(bars.CheapEstimate(30)).IsGreaterThan(0);
    }

    private static int Count(string text, char c)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == c)
            {
                n++;
            }
        }

        return n;
    }
}
