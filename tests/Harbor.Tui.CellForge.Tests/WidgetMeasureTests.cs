using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1209 (epic #1195) cell Measure/Render contract: <see cref="Measurement"/>
/// (Spectre Measurement analogue), <see cref="Padding"/> (Padder/IPaddable
/// analogue) and the Expand seating onto <see cref="FlexLayout"/> via
/// <see cref="WidgetMeasure.ToTrack"/> plus the <see cref="Panel"/> defaults.
/// Pure layout math — no painting, no viewport, no goldens touched.
/// </summary>
public sealed class WidgetMeasureTests
{
    private sealed class FixedNode(int width, int height) : LeafNode
    {
        public override Size Measure(Constraints constraints) => new(width, height);

        public override void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
        }
    }

    private sealed class TestPanel(string id, Size min) : Panel(id, min, 0)
    {
        public override void Paint(Harbor.Ui.Framework.Rendering.ScreenBuffer buffer)
        {
        }
    }

    private sealed class PaddedPanel(string id, Size min) : TestPanel(id, min)
    {
        public override Padding Padding => Padding.Uniform(1);
    }

    [Test]
    public async Task Measurement_Exact_NormalizesNegativeToZero()
    {
        await Assert.That(Measurement.Exact(-4)).IsEqualTo(new Measurement(0, 0));
        await Assert.That(Measurement.Exact(9)).IsEqualTo(new Measurement(9, 9));
    }

    [Test]
    public async Task Measurement_Create_OrdersMinMax()
    {
        await Assert.That(Measurement.Create(12, 5)).IsEqualTo(new Measurement(12, 12));
        await Assert.That(Measurement.Create(4, 9)).IsEqualTo(new Measurement(4, 9));
    }

    [Test]
    public async Task Measurement_Clamp_PinsBothEnds()
    {
        var m = Measurement.Create(4, 9);
        await Assert.That(m.Clamp(1)).IsEqualTo(4);
        await Assert.That(m.Clamp(6)).IsEqualTo(6);
        await Assert.That(m.Clamp(40)).IsEqualTo(9);
    }

    [Test]
    public async Task Measurement_Deflated_ShrinksByPaddingBill()
    {
        await Assert.That(Measurement.Create(4, 9).Deflated(2)).IsEqualTo(new Measurement(2, 7));
        await Assert.That(Measurement.Create(4, 9).Deflated(-3)).IsEqualTo(new Measurement(4, 9));
        await Assert.That(Measurement.Exact(1).Deflated(5)).IsEqualTo(new Measurement(0, 0));
    }

    [Test]
    public async Task Padding_Factories_Totals()
    {
        await Assert.That(Padding.Zero.Horizontal).IsEqualTo(0);
        await Assert.That(Padding.Zero.Vertical).IsEqualTo(0);
        await Assert.That(Padding.Uniform(2).Horizontal).IsEqualTo(4);
        await Assert.That(Padding.Uniform(2).Vertical).IsEqualTo(4);
        await Assert.That(Padding.Symmetric(3, 1).Horizontal).IsEqualTo(6);
        await Assert.That(Padding.Symmetric(3, 1).Vertical).IsEqualTo(2);
    }

    [Test]
    public async Task Padding_Deflate_YieldsContentRect()
    {
        var inner = Padding.Uniform(1).Deflate(new EngineCells.Rect(0, 0, 20, 5));
        await Assert.That(inner).IsEqualTo(new EngineCells.Rect(1, 1, 18, 3));
    }

    [Test]
    public async Task Padding_Deflate_Overpadded_CollapsesToZeroSize()
    {
        var inner = Padding.Uniform(4).Deflate(new EngineCells.Rect(5, 2, 6, 3));
        await Assert.That(inner).IsEqualTo(new EngineCells.Rect(9, 6, 0, 0));
    }

    [Test]
    public async Task Padding_NegativeInsets_CountAsZero()
    {
        var pad = new Padding(-2, -1, 0, 0);
        await Assert.That(pad.Horizontal).IsEqualTo(0);
        var inner = pad.Deflate(new EngineCells.Rect(0, 0, 20, 5));
        await Assert.That(inner).IsEqualTo(new EngineCells.Rect(0, 0, 20, 5));
    }

    [Test]
    public async Task Padding_Frame_InflatesAroundContent()
    {
        var outer = Padding.Symmetric(2, 1).Frame(4, 3, 10, 5);
        await Assert.That(outer).IsEqualTo(new EngineCells.Rect(2, 2, 14, 7));
    }

    [Test]
    public async Task Row_ExpandChild_AbsorbsRemainder_WithMeasuredFloor()
    {
        var label = new FixedNode(10, 5);
        var body = new FixedNode(4, 5);
        var row = new FlexSplitNode(SplitDir.Horizontal, gap: 1);
        row.Add(label, Measurement.Exact(10), expand: false);
        row.Add(body, Measurement.Create(4, 30), expand: true);
        row.Arrange(new EngineCells.Rect(0, 0, 40, 5));

        await Assert.That(label.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 10, 5));
        await Assert.That(body.Arranged).IsEqualTo(new EngineCells.Rect(11, 0, 29, 5));
    }

    [Test]
    public async Task Row_FixedChildren_TakeMeasuredFit_LeaveRemainder()
    {
        var first = new FixedNode(10, 5);
        var second = new FixedNode(6, 5);
        var row = new FlexSplitNode(SplitDir.Horizontal, gap: 1);
        row.Add(first, Measurement.Exact(10), expand: false);
        row.Add(second, Measurement.Create(2, 6), expand: false);
        row.Arrange(new EngineCells.Rect(0, 0, 40, 5));

        await Assert.That(first.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 10, 5));
        await Assert.That(second.Arranged).IsEqualTo(new EngineCells.Rect(11, 0, 6, 5));
    }

    [Test]
    public async Task Panel_Defaults_FixedMin_NoExpand_ZeroPadding()
    {
        var panel = new TestPanel("p", new Size(10, 3));
        await Assert.That(panel.Measure(80)).IsEqualTo(Measurement.Exact(10));
        await Assert.That(panel.Measure(5)).IsEqualTo(Measurement.Exact(5));
        await Assert.That(panel.Measure(-2)).IsEqualTo(Measurement.Exact(0));
        await Assert.That(panel.Expand).IsFalse();
        await Assert.That(panel.Padding).IsEqualTo(Padding.Zero);
    }

    [Test]
    public async Task Panel_Padding_IsOverridable()
    {
        Panel panel = new PaddedPanel("p", new Size(10, 3));
        await Assert.That(panel.Padding).IsEqualTo(Padding.Uniform(1));
        var content = panel.Padding.Deflate(new EngineCells.Rect(0, 0, 20, 5));
        await Assert.That(content).IsEqualTo(new EngineCells.Rect(1, 1, 18, 3));
    }
}
