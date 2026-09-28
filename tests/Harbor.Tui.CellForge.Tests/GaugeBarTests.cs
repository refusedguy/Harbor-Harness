using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GaugeBarTests
{
    private static string Art(int width, GaugeState state)
    {
        var buffer = new ScreenBuffer(width, 1);
        GaugeBar.Paint(buffer, new Rect(0, 0, width, 1), state);
        return GridDump.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task ClampRatio_PinsNaNAndOutOfRange()
    {
        await Assert.That(GaugeBar.ClampRatio(double.NaN)).IsEqualTo(0);
        await Assert.That(GaugeBar.ClampRatio(-2)).IsEqualTo(0);
        await Assert.That(GaugeBar.ClampRatio(2)).IsEqualTo(1);
        await Assert.That(GaugeBar.ClampRatio(0.25)).IsEqualTo(0.25);
    }

    [Test]
    public async Task FilledCells_RoundsProportionally()
    {
        await Assert.That(GaugeBar.FilledCells(0, 10)).IsEqualTo(0);
        await Assert.That(GaugeBar.FilledCells(0.5, 10)).IsEqualTo(5);
        await Assert.That(GaugeBar.FilledCells(1, 10)).IsEqualTo(10);
        await Assert.That(GaugeBar.FilledCells(0, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task LabelText_DefaultsToPercent()
    {
        await Assert.That(GaugeBar.LabelText(new GaugeState(0))).IsEqualTo("0%");
        await Assert.That(GaugeBar.LabelText(new GaugeState(0.424))).IsEqualTo("42%");
        await Assert.That(GaugeBar.LabelText(new GaugeState(0.425))).IsEqualTo("43%");
        await Assert.That(GaugeBar.LabelText(new GaugeState(1))).IsEqualTo("100%");
        await Assert.That(GaugeBar.LabelText(new GaugeState(0.5, "ctx"))).IsEqualTo("ctx");
    }

    [Test]
    public async Task Paint_Empty_RendersTrackWithZeroLabel()
    {
        string art = Art(10, new GaugeState(0, ShowLabel: false));
        await Assert.That(art).IsEqualTo(new string(GaugeBar.Track, 10));
    }

    [Test]
    public async Task Paint_Full_RendersFilledWithHundredLabel()
    {
        string art = Art(10, new GaugeState(1, ShowLabel: false));
        await Assert.That(art).IsEqualTo(new string(GaugeBar.Filled, 10));
    }

    [Test]
    public async Task Paint_Half_SplitsFillAndTrack()
    {
        string art = Art(10, new GaugeState(0.5, ShowLabel: false));
        await Assert.That(art).IsEqualTo(
            new string(GaugeBar.Filled, 5) + new string(GaugeBar.Track, 5));
    }

    [Test]
    public async Task Paint_DefaultLabel_CenteredOverBar()
    {
        // width 10, ratio 0 → label "0%" centered at columns 4-5.
        string art = Art(10, new GaugeState(0));
        await Assert.That(art.Contains("0%")).IsTrue();
        await Assert.That(art.Length).IsEqualTo(10);
    }

    [Test]
    public async Task Paint_CustomLabel_Centered()
    {
        string art = Art(9, new GaugeState(0.5, "hi"));
        await Assert.That(art.Contains("hi")).IsTrue();
    }

    [Test]
    public async Task Paint_ShowLabelFalse_OmitsLabel()
    {
        string art = Art(10, new GaugeState(0.5));
        string bare = Art(10, new GaugeState(0.5, ShowLabel: false));
        await Assert.That(art).IsNotEqualTo(bare);
        await Assert.That(bare.Contains("%")).IsFalse();
    }

    [Test]
    public async Task Paint_LabelWiderThanBar_OmitsLabel()
    {
        string art = Art(2, new GaugeState(0.5)); // "50%" needs 3 cells
        await Assert.That(art.Contains("%")).IsFalse();
        await Assert.That(art.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Paint_DegenerateRect_NoThrow()
    {
        var buffer = new ScreenBuffer(4, 1);
        GaugeBar.Paint(buffer, new Rect(0, 0, 0, 1), new GaugeState(0.5));
        GaugeBar.Paint(buffer, new Rect(0, 0, 4, 0), new GaugeState(0.5));
        await Assert.That(GridDump.Art(buffer).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Paint_LabelStyleFlips_AtFillBoundary()
    {
        // Ratio 0.5 over width 10 with a 2-cell label: label starts at 4,
        // so cell 4 is over fill, cell 5 over track → different style codes.
        var buffer = new ScreenBuffer(10, 1);
        var onFill = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var onTrack = ChatPalette.Dim;
        GaugeBar.Paint(buffer, new Rect(0, 0, 10, 1), new GaugeState(0.5, "ab"),
            labelOnFill: onFill, labelOnTrack: onTrack);
        string left = GridDump.StyleCode(buffer.Get(4, 0));
        string right = GridDump.StyleCode(buffer.Get(5, 0));
        await Assert.That(left).IsNotEqualTo(right);
    }
}
