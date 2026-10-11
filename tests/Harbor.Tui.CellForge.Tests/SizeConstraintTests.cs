using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// TR2 (#1221, epic #1195): SizeConstraint vocabulary (Fixed / Fill+weight /
/// Auto / Percent) and Auto-intrinsic lanes in the flex solver. Solver math
/// only — no painting, no viewport, no goldens touched.
/// </summary>
public sealed class SizeConstraintTests
{
    private sealed class FixedNode(int width, int height) : EngineCells.INode
    {
        public EngineCells.Rect Arranged { get; private set; }

        public Size Measure(Constraints constraints) =>
            new(Math.Min(Math.Max(0, width), constraints.Width), Math.Min(Math.Max(0, height), constraints.Height));

        public void Arrange(EngineCells.Rect rect) => Arranged = rect;

        public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
        }
    }

    [Test]
    public async Task Fixed_ClampsToAvailable()
    {
        await Assert.That(SizeConstraint.Exactly(6).Compute(10, 0, 0)).IsEqualTo(6);
        await Assert.That(SizeConstraint.Exactly(12).Compute(10, 0, 0)).IsEqualTo(10);
        await Assert.That(SizeConstraint.Exactly(-3).Compute(10, 0, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task Fill_TakesRemaining_NeverNegative()
    {
        await Assert.That(SizeConstraint.FillRemaining().Compute(24, 0, 20)).IsEqualTo(20);
        await Assert.That(SizeConstraint.FillRemaining().Compute(24, 0, 0)).IsEqualTo(0);
        await Assert.That(SizeConstraint.FillRemaining().Compute(24, 0, -5)).IsEqualTo(0);
    }

    [Test]
    public async Task FillRemaining_WeightFloorIsOne()
    {
        var constraint = (SizeConstraint.Fill)SizeConstraint.FillRemaining(0);

        await Assert.That(constraint.Weight).IsEqualTo(1);
    }

    [Test]
    public async Task Auto_SizesToContentWithinBounds()
    {
        await Assert.That(SizeConstraint.AutoSize().Compute(24, 7, 0)).IsEqualTo(7);
        await Assert.That(SizeConstraint.AutoSize(min: 10).Compute(24, 7, 0)).IsEqualTo(10);
        await Assert.That(SizeConstraint.AutoSize(max: 5).Compute(24, 7, 0)).IsEqualTo(5);
        await Assert.That(SizeConstraint.AutoSize().Compute(4, 7, 0)).IsEqualTo(4);
    }

    [Test]
    public async Task Percent_ShareOfAvailable_Clamped()
    {
        await Assert.That(SizeConstraint.Percentage(50).Compute(20, 0, 0)).IsEqualTo(10);
        await Assert.That(SizeConstraint.Percentage(150).Compute(20, 0, 0)).IsEqualTo(20);
        await Assert.That(SizeConstraint.Percentage(-10).Compute(20, 0, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task ToFlexTrack_RoundTripsThroughSolver()
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

        // Fixed 10 + Percent 25% of 40 (=10) + Fill 2/3 and 1/3 of the rest (20).
        await Assert.That(rects[0].Width).IsEqualTo(10);
        await Assert.That(rects[3].Width).IsEqualTo(10);
        await Assert.That(rects[1].Width).IsEqualTo(13);
        await Assert.That(rects[2].Width).IsEqualTo(7);
    }

    [Test]
    public async Task Auto_TakesIntrinsic_FillSplitsRemainder()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            [FlexTrack.Auto(), FlexTrack.Fill(1)],
            [8]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 8, 4),
            new EngineCells.Rect(8, 0, 22, 4),
        ]);
    }

    [Test]
    public async Task Auto_ClampsIntrinsicToMinMax()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            [FlexTrack.Auto(min: 4, max: 6), FlexTrack.Fill(1)],
            [20]);

        await Assert.That(rects[0].Width).IsEqualTo(6);
        await Assert.That(rects[1].Width).IsEqualTo(24);

        var floored = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 30, 4),
            [FlexTrack.Auto(min: 4, max: 6), FlexTrack.Fill(1)],
            [2]);

        await Assert.That(floored[0].Width).IsEqualTo(4);
        await Assert.That(floored[1].Width).IsEqualTo(26);
    }

    [Test]
    public async Task Auto_WithoutIntrinsic_DegradesToFloor()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 20, 4),
            [FlexTrack.Auto(min: 5), FlexTrack.Fill(1)]);

        await Assert.That(rects).IsEquivalentTo([
            new EngineCells.Rect(0, 0, 5, 4),
            new EngineCells.Rect(5, 0, 15, 4),
        ]);
    }

    [Test]
    public async Task Auto_Overflow_ScalesWithFixedShares()
    {
        var rects = FlexLayout.Solve(
            SplitDir.Horizontal,
            new EngineCells.Rect(0, 0, 10, 4),
            [FlexTrack.Auto(), FlexTrack.Fixed(8)],
            [6]);

        await Assert.That(rects[0].Width + rects[1].Width).IsEqualTo(10);
        await Assert.That(rects[0].Width).IsEqualTo(4);
        await Assert.That(rects[1].Width).IsEqualTo(6);
    }

    [Test]
    public async Task FlexSplitNode_AutoChild_KeepsIntrinsic_FillTakesRest()
    {
        var split = new FlexSplitNode(SplitDir.Horizontal);
        var auto = new FixedNode(8, 4);
        var fill = new FixedNode(1, 4);
        split.Add(auto, FlexTrack.Auto());
        split.Add(fill, FlexTrack.Fill(1));

        split.Arrange(new EngineCells.Rect(0, 0, 30, 4));

        await Assert.That(auto.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 8, 4));
        await Assert.That(fill.Arranged).IsEqualTo(new EngineCells.Rect(8, 0, 22, 4));
        await Assert.That(split.Measure(new Constraints(30, 4))).IsEqualTo(new EngineCells.Size(8, 4));
    }
}
