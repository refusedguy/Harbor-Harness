using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// PRIM8 (#303) flex-layout containers: linear Row/Column water-filling,
/// leftover justification, centered dialog boxes and full-area stacks.
/// Layout math only — no painting, no viewport, no goldens touched.
/// </summary>
public sealed class FlexLayoutTests
{
    [Test]
    public async Task Row_FixedFillFixed_SplitsRemainderWithGap()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 40, 10),
            [FlexTrack.Fixed(10), FlexTrack.Fill(1), FlexTrack.Fixed(6)],
            gap: 1);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 10, 10),
            new EngineCells.Rect(11, 0, 22, 10),
            new EngineCells.Rect(34, 0, 6, 10),
        ]);
    }

    [Test]
    public async Task Column_FixedFillFixed_StacksAlongY()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Vertical,
            new EngineCells.Rect(0, 0, 10, 40),
            [FlexTrack.Fixed(10), FlexTrack.Fill(1), FlexTrack.Fixed(6)],
            gap: 1);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 10, 10),
            new EngineCells.Rect(0, 11, 10, 22),
            new EngineCells.Rect(0, 34, 10, 6),
        ]);
    }

    [Test]
    public async Task Column_TwoPercents_ShareGapSubtractedExtent()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Vertical,
            new EngineCells.Rect(5, 2, 30, 20),
            [FlexTrack.Percent(0.25f), FlexTrack.Percent(0.75f)],
            gap: 2);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(5, 2, 30, 4),
            new EngineCells.Rect(5, 8, 30, 13),
        ]);
    }

    [Test]
    public async Task FillWeights_SplitRemainderProportionally()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 31, 6),
            [FlexTrack.Fill(1), FlexTrack.Fill(2)]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 10, 6),
            new EngineCells.Rect(10, 0, 21, 6),
        ]);
    }

    [Test]
    public async Task JustifyCenter_End_OffsetPackedRun()
    {
        var avail = new EngineCells.Rect(0, 0, 20, 4);
        FlexTrack[] tracks = [FlexTrack.Fixed(6), FlexTrack.Fixed(4)];

        var centered = FlexLayout.Solve(SplitDir.Horizontal, avail, tracks, gap: 2, justify: FlexJustify.Center);
        await Assert.That(centered).IsEquivalentTo([
            new EngineCells.Rect(4, 0, 6, 4),
            new EngineCells.Rect(12, 0, 4, 4),
        ]);

        var end = FlexLayout.Solve(SplitDir.Horizontal, avail, tracks, gap: 2, justify: FlexJustify.End);
        await Assert.That(end).IsEquivalentTo([
            new EngineCells.Rect(8, 0, 6, 4),
            new EngineCells.Rect(16, 0, 4, 4),
        ]);
    }

    [Test]
    public async Task SpaceBetween_PinsEdges_SpreadsGaps()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Fixed(6), FlexTrack.Fixed(4), FlexTrack.Fixed(2)],
            justify: FlexJustify.SpaceBetween);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(10, 0, 4, 4),
            new EngineCells.Rect(18, 0, 2, 4),
        ]);
    }

    [Test]
    public async Task SpaceAround_HalfShareEdges_FullShareGaps()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Fixed(4), FlexTrack.Fixed(4)],
            justify: FlexJustify.SpaceAround);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(3, 0, 4, 4),
            new EngineCells.Rect(13, 0, 4, 4),
        ]);
    }

    [Test]
    public async Task SpaceBetween_SingleTrack_PacksAtLeadingEdge()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Fixed(6)],
            justify: FlexJustify.SpaceBetween);

        await Assert.That(rects).IsEquivalentTo([new EngineCells.Rect(0, 0, 6, 4)]);
    }

    [Test]
    public async Task MinFloor_Honored_TrailingFlexAbsorbsOverflow()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Min(14), FlexTrack.Fill(1)]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 14, 4),
            new EngineCells.Rect(14, 0, 6, 4),
        ]);
    }

    [Test]
    public async Task MaxCap_Honored_HeadroomStaysLeftover()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Max(6), FlexTrack.Fill(1)]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(6, 0, 10, 4),
        ]);
    }

    [Test]
    public async Task Overflow_FixedTracks_ScaleDownProportionally()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            [FlexTrack.Fixed(21), FlexTrack.Fixed(20)]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 15, 4),
            new EngineCells.Rect(15, 0, 15, 4),
        ]);
    }

    [Test]
    public async Task OversizedGap_Collapses_AllTracksZeroSize()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 4, 4),
            [FlexTrack.Fixed(2), FlexTrack.Fixed(2)],
            gap: 5);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 0, 4),
            new EngineCells.Rect(0, 0, 0, 4),
        ]);
    }

    [Test]
    public async Task PercentNaN_MapsToZero_FillTakesRemainder()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Percent(float.NaN), FlexTrack.Fill(1)]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 0, 4),
            new EngineCells.Rect(0, 0, 20, 4),
        ]);
    }

    [Test]
    public async Task EmptyTracks_ReturnsEmpty()
    {
        var rects = FlexLayout.Solve(SplitDir.Horizontal, new EngineCells.Rect(0, 0, 20, 4), []);

        await Assert.That(rects.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Arrange_NegativeGap_Throws()
    {
        FlexTrack[] tracks = [FlexTrack.Fixed(4)];

        await Assert.That(() => FlexLayout.Solve(SplitDir.Horizontal, new EngineCells.Rect(0, 0, 20, 4), tracks, gap: -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Arrange_ShortDestinationSpan_Throws()
    {
        FlexTrack[] tracks = [FlexTrack.Fixed(4), FlexTrack.Fixed(4)];
        var into = new EngineCells.Rect[1];

        await Assert.That(() => FlexLayout.Arrange(SplitDir.Horizontal, new EngineCells.Rect(0, 0, 20, 4), tracks, into))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Center_FitsBox_AtMidpoint()
    {
        var rect = CenterLayout.Arrange(new EngineCells.Rect(0, 0, 40, 10), 10, 4);

        await Assert.That(rect).IsEqualTo(new EngineCells.Rect(15, 3, 10, 4));
    }

    [Test]
    public async Task Center_OversizedContent_ClampsToAvail()
    {
        var avail = new EngineCells.Rect(0, 0, 40, 10);

        var rect = CenterLayout.Arrange(avail, new EngineCells.Size(100, 50));

        await Assert.That(rect).IsEqualTo(avail);
    }

    [Test]
    public async Task Stack_EveryChild_TakesFullArea()
    {
        var avail = new EngineCells.Rect(2, 3, 10, 5);

        var rects = StackLayout.Solve(avail, 3);

        await Assert.That(rects).IsEquivalentTo([avail, avail, avail]);
    }

    [Test]
    public async Task Stack_ZeroCount_ReturnsEmpty()
    {
        var rects = StackLayout.Solve(new EngineCells.Rect(0, 0, 10, 5), 0);

        await Assert.That(rects.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Stack_NegativeCount_Throws()
    {
        await Assert.That(() => StackLayout.Solve(new EngineCells.Rect(0, 0, 10, 5), -1))
            .Throws<ArgumentOutOfRangeException>();
    }
}
