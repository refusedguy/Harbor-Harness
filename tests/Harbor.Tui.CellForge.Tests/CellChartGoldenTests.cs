using Harbor.TestKit;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #2382 golden frames: the whole 16x6 art of each chart mode, stored under
// tests/fixtures/chart. The baseline is compared byte-for-byte after line
// ending normalisation; CI never regenerates it.
[NotInParallel("pty")]
public class CellChartGoldenTests
{
    private static readonly double[] Ramp = [0, 2, 4, 6, 8];
    private static readonly string[] Times = ["t0", "t1", "t2", "t3", "t4"];

    private static string Frame(CellChartMode mode, string title)
    {
        var state = new CellChartState(new ChartSeries(Ramp, Times), Mode: mode, Title: title);
        var buffer = TestRender.Buffer(16, 6);
        CellChart.Paint(buffer, new Rect(0, 0, 16, 6), state);
        return TestRender.Art(buffer);
    }

    [Test]
    public async Task Golden_Bars_Frame()
    {
        string art = Frame(CellChartMode.Bars, "req/s");
        string expected = Golden.VerifyText(Golden.FixtureDir("chart"), "CellChart_Bars", art);
        await Assert.That(art).IsEqualTo(expected);
        await Assert.That(art).Contains("req/s");
    }

    [Test]
    public async Task Golden_Sparkline_Frame()
    {
        string art = Frame(CellChartMode.Sparkline, "tok/s");
        string expected = Golden.VerifyText(Golden.FixtureDir("chart"), "CellChart_Sparkline", art);
        await Assert.That(art).IsEqualTo(expected);
        await Assert.That(art).Contains("tok/s");
    }
}
