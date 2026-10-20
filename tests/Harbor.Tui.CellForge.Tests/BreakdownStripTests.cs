using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class BreakdownStripTests
{
    private static string Art(int width, IReadOnlyList<BreakdownSegment> segs)
    {
        var buffer = TestRender.Buffer(width, 1);
        BreakdownStrip.Paint(buffer, new Rect(0, 0, width, 1), segs);
        return TestRender.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task CellCounts_SumToWidthExactly()
    {
        var segs = new List<BreakdownSegment> { new("a", 1), new("b", 1), new("c", 1) };
        var counts = new int[3];
        BreakdownStrip.CellCounts(segs, 10, counts);
        await Assert.That(counts[0] + counts[1] + counts[2]).IsEqualTo(10);
    }

    [Test]
    public async Task Paint_HalfHalf_SplitsFill()
    {
        string art = Art(10, [new("a", 1), new("b", 1)]);
        await Assert.That(art.Length).IsEqualTo(10);
        await Assert.That(art.Contains(new string(GaugeBar.Track, 1))).IsFalse();
    }

    [Test]
    public async Task Paint_ZeroTotal_RendersTrack()
    {
        string art = Art(6, [new("a", 0), new("b", 0)]);
        await Assert.That(art).IsEqualTo(new string(GaugeBar.Track, 6));
    }

    [Test]
    public async Task Paint_Empty_RendersTrack()
    {
        string art = Art(4, []);
        await Assert.That(art).IsEqualTo(new string(GaugeBar.Track, 4));
    }

    [Test]
    public async Task Paint_Degenerate_NoThrow()
    {
        var buffer = TestRender.Buffer(4, 1);
        BreakdownStrip.Paint(buffer, new Rect(0, 0, 0, 1), [new("a", 1)]);
        await Assert.That(TestRender.Art(buffer).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task TagText_SharesOfTotal()
    {
        string tags = BreakdownStrip.TagText([new("in", 1), new("out", 3)]);
        await Assert.That(tags.Contains("in 25%")).IsTrue();
        await Assert.That(tags.Contains("out 75%")).IsTrue();
    }

    [Test]
    public async Task Sanitize_PinsNaNAndNegative()
    {
        await Assert.That(BreakdownStrip.Sanitize(double.NaN)).IsEqualTo(0);
        await Assert.That(BreakdownStrip.Sanitize(-5)).IsEqualTo(0);
        await Assert.That(BreakdownStrip.Sanitize(2.5)).IsEqualTo(2.5);
    }
}
