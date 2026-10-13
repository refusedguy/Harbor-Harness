using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Doc-tests as spec (#1169): each test mirrors an <c>example</c> ASCII
/// diagram from the <see cref="FlexLayout"/> / <see cref="FlexJustify"/> /
/// <see cref="FlexLayoutExplorer"/> doc-comments, asserting both the rects
/// and the explorer frame so docs and solver cannot drift apart.
/// </summary>
public sealed class FlexLayoutDocTests
{
    [Test]
    public async Task Doc_Row_FixedFillFixed_Gap()
    {
        FlexTrack[] tracks = [FlexTrack.Fixed(10), FlexTrack.Fill(1), FlexTrack.Fixed(6)];
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 40, 10),
            tracks,
            gap: 1);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 10, 10),
            new EngineCells.Rect(11, 0, 22, 10),
            new EngineCells.Rect(34, 0, 6, 10),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(40, tracks, gap: 1))
            .IsEqualTo("[AAAAAAAAAA BBBBBBBBBBBBBBBBBBBBBB CCCCCC]");
    }

    [Test]
    public async Task Doc_Justify_Start_Center_End()
    {
        var avail = new EngineCells.Rect(0, 0, 20, 4);
        FlexTrack[] tracks = [FlexTrack.Fixed(6), FlexTrack.Fixed(4)];

        var start = FlexLayout.Solve(SplitDir.Horizontal, avail, tracks, gap: 2);
        await Assert.That(start).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(8, 0, 4, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, tracks, gap: 2))
            .IsEqualTo("[AAAAAA  BBBB        ]");

        var centered = FlexLayout.Solve(SplitDir.Horizontal, avail, tracks, gap: 2, justify: FlexJustify.Center);
        await Assert.That(centered).IsEquivalentTo([
            new EngineCells.Rect(4, 0, 6, 4),
            new EngineCells.Rect(12, 0, 4, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, tracks, gap: 2, justify: FlexJustify.Center))
            .IsEqualTo("[    AAAAAA  BBBB    ]");

        var end = FlexLayout.Solve(SplitDir.Horizontal, avail, tracks, gap: 2, justify: FlexJustify.End);
        await Assert.That(end).IsEquivalentTo([
            new EngineCells.Rect(8, 0, 6, 4),
            new EngineCells.Rect(16, 0, 4, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, tracks, gap: 2, justify: FlexJustify.End))
            .IsEqualTo("[        AAAAAA  BBBB]");
    }

    [Test]
    public async Task Doc_SpaceBetween_PinsEdges()
    {
        FlexTrack[] tracks = [FlexTrack.Fixed(6), FlexTrack.Fixed(4), FlexTrack.Fixed(2)];
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            tracks,
            justify: FlexJustify.SpaceBetween);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(10, 0, 4, 4),
            new EngineCells.Rect(18, 0, 2, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, tracks, justify: FlexJustify.SpaceBetween))
            .IsEqualTo("[AAAAAA    BBBB    CC]");
    }

    [Test]
    public async Task Doc_SpaceAround_And_SpaceEvenly()
    {
        FlexTrack[] pair = [FlexTrack.Fixed(4), FlexTrack.Fixed(4)];

        var around = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            pair,
            justify: FlexJustify.SpaceAround);
        await Assert.That(around).IsEquivalentTo([
            new EngineCells.Rect(3, 0, 4, 4),
            new EngineCells.Rect(13, 0, 4, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, pair, justify: FlexJustify.SpaceAround))
            .IsEqualTo("[   AAAA      BBBB   ]");

        FlexTrack[] sixFour = [FlexTrack.Fixed(6), FlexTrack.Fixed(4)];
        var evenly = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            sixFour,
            justify: FlexJustify.SpaceEvenly);
        await Assert.That(evenly).IsEquivalentTo([
            new EngineCells.Rect(4, 0, 6, 4),
            new EngineCells.Rect(13, 0, 4, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, sixFour, justify: FlexJustify.SpaceEvenly))
            .IsEqualTo("[    AAAAAA   BBBB   ]");
    }

    [Test]
    public async Task Doc_Legacy_FillWeakest()
    {
        FlexTrack[] fixed_ = [FlexTrack.Fixed(6), FlexTrack.Fixed(4)];
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            fixed_,
            justify: FlexJustify.Legacy);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(6, 0, 14, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, fixed_, justify: FlexJustify.Legacy))
            .IsEqualTo("[AAAAAABBBBBBBBBBBBBB]");

        FlexTrack[] capped = [FlexTrack.Max(6), FlexTrack.Fill(1)];
        var fill = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            capped,
            justify: FlexJustify.Legacy);
        await Assert.That(fill).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(6, 0, 14, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, capped, justify: FlexJustify.Legacy))
            .IsEqualTo("[AAAAAABBBBBBBBBBBBBB]");
    }

    [Test]
    public async Task Doc_FillWeights_Proportional()
    {
        FlexTrack[] tracks = [FlexTrack.Fill(1), FlexTrack.Fill(2)];
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 31, 6),
            tracks);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 10, 6),
            new EngineCells.Rect(10, 0, 21, 6),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(31, tracks))
            .IsEqualTo("[AAAAAAAAAABBBBBBBBBBBBBBBBBBBBB]");
    }

    [Test]
    public async Task Doc_Auto_Intrinsic_And_MinMax()
    {
        FlexTrack[] tracks = [FlexTrack.Auto(), FlexTrack.Fill(1)];
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            tracks,
            [8]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 8, 4),
            new EngineCells.Rect(8, 0, 22, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(30, tracks, [8]))
            .IsEqualTo("[AAAAAAAABBBBBBBBBBBBBBBBBBBBBB]");

        FlexTrack[] bounded = [FlexTrack.Auto(min: 4, max: 6), FlexTrack.Fill(1)];
        var capped = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            bounded,
            [20]);
        await Assert.That(capped[0].Width).IsEqualTo(6);
        await Assert.That(capped[1].Width).IsEqualTo(24);
        await Assert.That(FlexLayoutExplorer.Render(30, bounded, [20]))
            .IsEqualTo("[AAAAAABBBBBBBBBBBBBBBBBBBBBBBB]");

        var floored = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            bounded,
            [2]);
        await Assert.That(floored[0].Width).IsEqualTo(4);
        await Assert.That(floored[1].Width).IsEqualTo(26);
        await Assert.That(FlexLayoutExplorer.Render(30, bounded, [2]))
            .IsEqualTo("[AAAABBBBBBBBBBBBBBBBBBBBBBBBBB]");
    }

    [Test]
    public async Task Doc_MinFloor_MaxCap()
    {
        FlexTrack[] floored = [FlexTrack.Min(14), FlexTrack.Fill(1)];
        var min = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            floored);

        await Assert.That(min).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 14, 4),
            new EngineCells.Rect(14, 0, 6, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, floored))
            .IsEqualTo("[AAAAAAAAAAAAAABBBBBB]");

        FlexTrack[] capped = [FlexTrack.Max(6), FlexTrack.Fill(1)];
        var max = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            capped);

        await Assert.That(max).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 6, 4),
            new EngineCells.Rect(6, 0, 10, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, capped))
            .IsEqualTo("[AAAAAABBBBBBBBBB    ]");
    }

    [Test]
    public async Task Doc_GapCollapse_And_NaN()
    {
        FlexTrack[] collapsed = [FlexTrack.Fixed(2), FlexTrack.Fixed(2)];
        var gap = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 4, 4),
            collapsed,
            gap: 5);

        await Assert.That(gap).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 0, 4),
            new EngineCells.Rect(0, 0, 0, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(4, collapsed, gap: 5))
            .IsEqualTo("[    ]");

        FlexTrack[] nan = [FlexTrack.Percent(float.NaN), FlexTrack.Fill(1)];
        var filled = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            nan);

        await Assert.That(filled).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 0, 4),
            new EngineCells.Rect(0, 0, 20, 4),
        ]);
        await Assert.That(FlexLayoutExplorer.Render(20, nan))
            .IsEqualTo("[BBBBBBBBBBBBBBBBBBBB]");
    }

    [Test]
    public async Task Doc_Percent_RoundTrip()
    {
        FlexTrack[] tracks =
        [
            SizeConstraint.Exactly(10).ToFlexTrack(),
            SizeConstraint.FillRemaining(2).ToFlexTrack(),
            SizeConstraint.FillRemaining(1).ToFlexTrack(),
            SizeConstraint.Percentage(25).ToFlexTrack(),
        ];

        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 40, 4),
            tracks);

        await Assert.That(rects[0].Width).IsEqualTo(10);
        await Assert.That(rects[3].Width).IsEqualTo(10);
        await Assert.That(rects[1].Width).IsEqualTo(13);
        await Assert.That(rects[2].Width).IsEqualTo(7);
        await Assert.That(FlexLayoutExplorer.Render(40, tracks))
            .IsEqualTo("[AAAAAAAAAABBBBBBBBBBBBBCCCCCCCDDDDDDDDDD]");
    }
}
