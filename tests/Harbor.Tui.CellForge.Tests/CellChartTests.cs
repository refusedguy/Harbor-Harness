using Harbor.TestKit;
using Harbor.Ui.Framework.Rendering.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #2382 cell chart: the numeric adapter, scale/axis helpers, time zoom and
// both paint modes. Rendering reads global TerminalColorPalette, so this
// class stays off the pty lane like GaugeBarTests and SparklineBlockTests.
[NotInParallel("pty")]
public class CellChartTests
{
    private static readonly double[] Ramp = [0, 2, 4, 6, 8];
    private static readonly string[] Times = ["t0", "t1", "t2", "t3", "t4"];

    private static CellChartState Bars(string? title = "req/s") =>
        new(new ChartSeries(Ramp, Times), Title: title);

    private static string Art(int width, int height, CellChartState state)
    {
        var buffer = TestRender.Buffer(width, height);
        CellChart.Paint(buffer, new Rect(0, 0, width, height), state);
        return TestRender.Art(buffer);
    }

    [Test]
    public async Task Sanitize_PinsNonFiniteAndNegative()
    {
        await Assert.That(CellChart.Sanitize(double.NaN)).IsEqualTo(0);
        await Assert.That(CellChart.Sanitize(double.NegativeInfinity)).IsEqualTo(0);
        await Assert.That(CellChart.Sanitize(-5)).IsEqualTo(0);
        await Assert.That(CellChart.Sanitize(double.PositiveInfinity)).IsGreaterThan(0);
        await Assert.That(CellChart.Sanitize(2.5)).IsEqualTo(2.5);
    }

    [Test]
    public async Task NormalizeWindow_ClampsToSeries()
    {
        var series = new ChartSeries(Ramp);
        await Assert.That(CellChart.NormalizeWindow(series, new ChartWindow(-3, 99)))
            .IsEqualTo(new ChartWindow(0, 5));
        await Assert.That(CellChart.NormalizeWindow(series, new ChartWindow(3, 5)))
            .IsEqualTo(new ChartWindow(3, 2));
        await Assert.That(CellChart.NormalizeWindow(default, null)).IsEqualTo(default);
    }

    [Test]
    public async Task VisibleValues_AppliesZoomAndSanitizes()
    {
        var state = new CellChartState(
            new ChartSeries([1, 2, double.NaN, 4, 5]),
            Window: new ChartWindow(1, 3));
        var values = CellChart.VisibleValues(state);
        await Assert.That(values.Count).IsEqualTo(3);
        await Assert.That(values[0]).IsEqualTo(2);
        await Assert.That(values[1]).IsEqualTo(0);
        await Assert.That(values[2]).IsEqualTo(4);
    }

    [Test]
    public async Task Scale_UsesZeroBaselineAndUnitFloor()
    {
        var scaled = CellChart.Scale([0, 4, -2, double.NaN]);
        await Assert.That(scaled.Min).IsEqualTo(0);
        await Assert.That(scaled.Max).IsEqualTo(4);
        var flat = CellChart.Scale([0, 0]);
        await Assert.That(flat.Min).IsEqualTo(0);
        await Assert.That(flat.Max).IsEqualTo(1);
    }

    [Test]
    public async Task YLabels_AreHighToLowAndInvariant()
    {
        var labels = CellChart.YLabels(Bars());
        await Assert.That(labels).IsEquivalentTo(new[] { "8", "4", "0" });
        await Assert.That(CellChart.FormatTick(2.5)).IsEqualTo("2.5");
    }

    [Test]
    public async Task XLabels_UseTimeCaptionsForVisibleRange()
    {
        var state = Bars() with { Window = new ChartWindow(1, 3) };
        var (left, right) = CellChart.XLabels(state);
        await Assert.That(left).IsEqualTo("t1");
        await Assert.That(right).IsEqualTo("t3");

        var (indexedLeft, indexedRight) = CellChart.XLabels(new CellChartState(new ChartSeries(Ramp)));
        await Assert.That(indexedLeft).IsEqualTo("0");
        await Assert.That(indexedRight).IsEqualTo("4");
    }

    [Test]
    public async Task Bucket_KeepsPeaksWhenSamplesExceedColumns()
    {
        await Assert.That(CellChart.Bucket(Ramp, 3)).IsEquivalentTo(new[] { 0d, 4d, 8d });
        await Assert.That(CellChart.Bucket(Ramp, 0)).IsEmpty();
    }

    [Test]
    public async Task BarRows_RoundsAgainstPlotHeight()
    {
        await Assert.That(CellChart.BarRows(0, 8, 4)).IsEqualTo(0);
        await Assert.That(CellChart.BarRows(2, 8, 4)).IsEqualTo(1);
        await Assert.That(CellChart.BarRows(6, 8, 4)).IsEqualTo(3);
        await Assert.That(CellChart.BarRows(20, 8, 4)).IsEqualTo(4);
    }

    [Test]
    public async Task Slice_ReturnsVisibleWindowValuesAndCaptions()
    {
        var series = new ChartSeries([1, 2, 3, 4, 5], ["a", "b", "c", "d", "e"]);
        var sliced = series.Slice(new ChartWindow(1, 3));
        await Assert.That(sliced.Values).IsEquivalentTo(new[] { 2d, 3d, 4d });
        await Assert.That(sliced.TimeLabels).IsEquivalentTo(new[] { "b", "c", "d" });
    }

    [Test]
    public async Task ZoomIn_ZoomOut_Pan_FoldTheWindow()
    {
        var state = Bars() with { Window = new ChartWindow(1, 4), Title = null };
        var zoomedIn = CellChart.ZoomIn(state);
        await Assert.That(zoomedIn.Window).IsEqualTo(new ChartWindow(2, 2));

        var zoomedOut = CellChart.ZoomOut(state);
        await Assert.That(zoomedOut.Window).IsEqualTo(new ChartWindow(0, 5));

        var panned = CellChart.Pan(state, 1);
        await Assert.That(panned.Window).IsEqualTo(new ChartWindow(1, 4));
        await Assert.That(CellChart.Pan(panned, 99).Window).IsEqualTo(panned.Window);
        await Assert.That(CellChart.ZoomIn(zoomedIn with { Window = new ChartWindow(0, 1) }).Window)
            .IsEqualTo(new ChartWindow(0, 1));
    }

    [Test]
    public async Task Paint_DegenerateRect_PaintsNothing()
    {
        var buffer = TestRender.Buffer(4, 2);
        CellChart.Paint(buffer, new Rect(0, 0, 0, 2), Bars());
        CellChart.Paint(buffer, new Rect(0, 0, 4, 0), Bars());
        await Assert.That(TestRender.Art(buffer)).IsEqualTo("    \n    \n");
    }

    [Test]
    public async Task Paint_Bars_RendersRampWithBothAxes()
    {
        string expected =
            "req/s" + new string(' ', 11) + "\n" +
            "8" + new string(' ', 13) + "██\n" +
            new string(' ', 11) + "█████\n" +
            "4" + new string(' ', 7) + "████████\n" +
            "0" + new string(' ', 4) + "███████████\n" +
            "t0" + new string(' ', 12) + "t4\n";
        await Assert.That(Art(16, 6, Bars())).IsEqualTo(expected);
    }

    [Test]
    public async Task Paint_Sparkline_ReusesSparklineBlockGlyphs()
    {
        var state = Bars("tok/s") with { Mode = CellChartMode.Sparkline };
        string art = Art(16, 6, state);
        await Assert.That(art).Contains(SparklineBlock.MapToGlyphs(Ramp, 8));
        await Assert.That(art).Contains("tok/s");
        await Assert.That(art).Contains("t4");
    }
}
