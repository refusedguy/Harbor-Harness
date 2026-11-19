// #3088 (slice S7): public Layout.Split facade. Layout math only — no
// painting, no viewport, no goldens touched. Covers fixed + proportional
// shares, the global minimum panel size and the degenerate inputs.

using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Public <see cref="Layout.Split(Rect, SplitDir, SizeConstraint[], int)"/>
/// primitive: horizontal/vertical carve, fixed and proportional shares, a
/// minimum panel floor, and the zero/negative/overflow edge cases.
/// </summary>
public sealed class LayoutSplitTests
{
    [Test]
    public async Task Horizontal_FixedFillFixed_SplitsRemainder()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 40, 10),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(10), SizeConstraint.FillRemaining(), SizeConstraint.Exactly(6)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 10, 10),
            new Rect(10, 0, 24, 10),
            new Rect(34, 0, 6, 10),
        ]);
    }

    [Test]
    public async Task Vertical_FixedFillFixed_StacksAlongY_AndCrossAxisFills()
    {
        var rects = Layout.Split(
            new Rect(5, 2, 10, 40),
            SplitDir.Vertical,
            [SizeConstraint.Exactly(10), SizeConstraint.FillRemaining(), SizeConstraint.Exactly(6)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(5, 2, 10, 10),
            new Rect(5, 12, 10, 24),
            new Rect(5, 36, 10, 6),
        ]);
    }

    [Test]
    public async Task Ratios_SplitByFraction()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 30, 4),
            SplitDir.Horizontal,
            [SizeConstraint.RatioOf(1, 3), SizeConstraint.RatioOf(2, 3)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 10, 4),
            new Rect(10, 0, 20, 4),
        ]);
    }

    [Test]
    public async Task Percents_SplitByFraction()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 40, 2),
            SplitDir.Horizontal,
            [SizeConstraint.Percentage(25), SizeConstraint.Percentage(75)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 10, 2),
            new Rect(10, 0, 30, 2),
        ]);
    }

    [Test]
    public async Task FillWeights_SplitLeftoverProportionally()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 31, 6),
            SplitDir.Horizontal,
            [SizeConstraint.FillRemaining(1), SizeConstraint.FillRemaining(2)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 10, 6),
            new Rect(10, 0, 21, 6),
        ]);
    }

    [Test]
    public async Task Mixed_FixedPercentAndFill_ResolveInThatOrder()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 100, 2),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(10), SizeConstraint.Percentage(50), SizeConstraint.FillRemaining()]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 10, 2),
            new Rect(10, 0, 50, 2),
            new Rect(50, 0, 40, 2),
        ]);
    }

    [Test]
    public async Task Minimum_RaisesSmallFixedAndShrinksTheFiller()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 20, 6),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(4), SizeConstraint.FillRemaining()],
            minPanelSize: 8);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 8, 6),
            new Rect(8, 0, 12, 6),
        ]);
    }

    [Test]
    public async Task Minimum_OverflowKeepsTheFloorAndScalesExcess()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 10, 4),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(8), SizeConstraint.Exactly(8)],
            minPanelSize: 3);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 5, 4),
            new Rect(5, 0, 5, 4),
        ]);
    }

    [Test]
    public async Task Minimum_InfeasibleFloor_IsDropped()
    {
        // 2 panels * 6 > 10: the floor cannot fit, so the split degrades to a
        // plain fixed + filler carve instead of overflowing the area.
        var rects = Layout.Split(
            new Rect(0, 0, 10, 4),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(2), SizeConstraint.FillRemaining()],
            minPanelSize: 6);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 2, 4),
            new Rect(2, 0, 8, 4),
        ]);
    }

    [Test]
    public async Task Minimum_NegativeValue_CountsAsZero()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 20, 4),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(4), SizeConstraint.FillRemaining()],
            minPanelSize: -5);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 4, 4),
            new Rect(4, 0, 16, 4),
        ]);
    }

    [Test]
    public async Task Degenerate_NoShares_ReturnsEmpty()
    {
        var rects = Layout.Split(new Rect(0, 0, 40, 10), SplitDir.Horizontal, []);

        await Assert.That(rects).IsEmpty();
    }

    [Test]
    public async Task Degenerate_ZeroOrNegativeExtent_YieldsZeroSizePanels()
    {
        var zero = Layout.Split(
            new Rect(0, 0, 0, 10),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(5), SizeConstraint.FillRemaining()],
            minPanelSize: 3);

        await Assert.That(zero).IsEquivalentTo([
            new Rect(0, 0, 0, 10),
            new Rect(0, 0, 0, 10),
        ]);

        var negative = Layout.Split(
            new Rect(2, 3, -5, -2),
            SplitDir.Vertical,
            [SizeConstraint.FillRemaining()]);

        await Assert.That(negative).IsEquivalentTo([new Rect(2, 3, 0, 0)]);
    }

    [Test]
    public async Task Degenerate_SingleOversizedFixed_ClampedToExtent()
    {
        var rects = Layout.Split(
            new Rect(3, 4, 10, 5),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(100)]);

        await Assert.That(rects).IsEquivalentTo([new Rect(3, 4, 10, 5)]);
    }

    [Test]
    public async Task Degenerate_InvalidRatiosAndOverPercent_AreClamped()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 20, 2),
            SplitDir.Horizontal,
            [SizeConstraint.RatioOf(1, 0), SizeConstraint.Percentage(150)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 0, 2),
            new Rect(0, 0, 20, 2),
        ]);
    }

    [Test]
    public async Task Degenerate_AllFixedUnderfill_LeavesSpaceTrailing()
    {
        var rects = Layout.Split(
            new Rect(0, 0, 20, 2),
            SplitDir.Horizontal,
            [SizeConstraint.Exactly(3), SizeConstraint.Exactly(4)]);

        await Assert.That(rects).IsEquivalentTo([
            new Rect(0, 0, 3, 2),
            new Rect(3, 0, 4, 2),
        ]);
    }

    [Test]
    public async Task SpanOverload_WritesIntoCallerBuffer_AndGuardsItsLength()
    {
        SizeConstraint[] shares = [SizeConstraint.Exactly(5), SizeConstraint.FillRemaining()];
        var into = new Rect[2];

        Layout.Split(new Rect(0, 0, 20, 4), SplitDir.Horizontal, shares, into);

        await Assert.That(into).IsEquivalentTo([
            new Rect(0, 0, 5, 4),
            new Rect(5, 0, 15, 4),
        ]);

        await Assert.That(() => Layout.Split(new Rect(0, 0, 20, 4), SplitDir.Horizontal, shares, new Rect[1]))
            .Throws<ArgumentException>();
    }
}
