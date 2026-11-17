using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2360 (epic #1195): measured layout leaves over <see cref="AvailableSpace"/>
/// (Definite / MinContent / MaxContent) and their seating in the ONE flex
/// solver from #1221 (<see cref="FlexTrack.Auto"/> floor/cap + intrinsic
/// two-pass). No second solver: min/max-content only feed the Auto track.
/// Pure layout math — no painting, no viewport, no goldens touched.
/// </summary>
public sealed class MeasuredLeafTests
{
    // "alpha beta gamma": longest word 5, full line 16, one word per line = 3.
    private static Size WordWrap(AvailableSpace space)
    {
        const int longestWord = 5;
        const int fullLine = 16;
        if (space.IsMinContent)
        {
            return new Size(longestWord, 3);
        }

        if (space.IsMaxContent)
        {
            return new Size(fullLine, 1);
        }

        int width = Math.Min(space.Width, fullLine);
        int perLine = Math.Max(width, longestWord);
        int lines = (fullLine + perLine - 1) / perLine;
        return new Size(width, lines);
    }

    // Two columns: col1 min 3 / max 8, col2 min 4 / max 7, two data rows.
    private static Size Table(AvailableSpace space)
    {
        const int minWidth = 3 + 4;
        const int maxWidth = 8 + 7;
        if (space.IsMinContent)
        {
            return new Size(minWidth, 2);
        }

        if (space.IsMaxContent)
        {
            return new Size(maxWidth, 2);
        }

        return new Size(Math.Min(space.Width, maxWidth), 2);
    }

    private sealed class StubNode : LeafNode
    {
        public override Size Measure(Constraints constraints) => new(0, 0);

        public override void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
        }
    }

    [Test]
    public async Task AvailableSpace_Factories_ClampDefiniteAndFlagSemantics()
    {
        var definite = AvailableSpace.Definite(-4, 9);
        await Assert.That(definite.IsDefinite).IsTrue();
        await Assert.That(definite.Width).IsEqualTo(0);
        await Assert.That(definite.Height).IsEqualTo(9);

        var min = AvailableSpace.MinContent;
        await Assert.That(min.IsMinContent).IsTrue();
        await Assert.That(min.IsDefinite).IsFalse();
        await Assert.That(min.Width).IsEqualTo(0);
        await Assert.That(min.Height).IsEqualTo(0);

        var max = AvailableSpace.MaxContent;
        await Assert.That(max.IsMaxContent).IsTrue();
        await Assert.That(max.IsMinContent).IsFalse();

        await Assert.That(default(AvailableSpace).Kind).IsEqualTo(AvailableSpaceKind.Definite);
    }

    [Test]
    public async Task MeasuredLeaf_DefiniteMeasure_DelegatesToClosure()
    {
        var leaf = new MeasuredLeaf(WordWrap);

        await Assert.That(leaf.Measure(new Constraints(8, 100))).IsEqualTo(new Size(8, 2));
        await Assert.That(leaf.Measure(new Constraints(20, 100))).IsEqualTo(new Size(16, 1));
        await Assert.That(leaf.Measure(new Constraints(-3, -3))).IsEqualTo(new Size(0, 4));
    }

    [Test]
    public async Task MeasuredLeaf_MinAndMaxContent_ReportIntrinsicExtents()
    {
        var leaf = new MeasuredLeaf(WordWrap);

        await Assert.That(leaf.MinContent).IsEqualTo(new Size(5, 3));
        await Assert.That(leaf.MaxContent).IsEqualTo(new Size(16, 1));
    }

    [Test]
    public async Task MeasuredLeaf_ToTrack_MapsMinMaxContentToAutoFloorCap()
    {
        var leaf = new MeasuredLeaf(WordWrap);

        FlexTrack track = leaf.ToTrack(SplitDir.Horizontal);
        await Assert.That(track.IsAuto).IsTrue();
        await Assert.That(track.FlexFloor).IsEqualTo(5);
        await Assert.That(track.FlexCap).IsEqualTo(16);
    }

    [Test]
    public async Task Row_MeasuredLeaf_MaxContentCapsWidth_FillTakesHeadroom()
    {
        var leaf = new MeasuredLeaf(WordWrap);
        var fill = new StubNode();
        var row = new FlexSplitNode(SplitDir.Horizontal);
        row.AddMeasured(leaf); // Auto(5, 16)
        row.Add(fill, FlexTrack.Fill(1));

        row.Arrange(new EngineCells.Rect(0, 0, 40, 10));

        await Assert.That(leaf.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 16, 10));
        await Assert.That(fill.Arranged).IsEqualTo(new EngineCells.Rect(16, 0, 24, 10));
    }

    [Test]
    public async Task Row_MeasuredLeaf_Shrink_FillCollapses_MeasuredHoldsMinContent()
    {
        var leaf = new MeasuredLeaf(WordWrap);
        var fill = new StubNode();
        var row = new FlexSplitNode(SplitDir.Horizontal);
        row.AddMeasured(leaf);
        row.Add(fill, FlexTrack.Fill(1));

        row.Arrange(new EngineCells.Rect(0, 0, 7, 10)); // exactly min-content

        await Assert.That(leaf.Arranged.Width).IsEqualTo(7);
        await Assert.That(fill.Arranged.Width).IsEqualTo(0);

        row.Arrange(new EngineCells.Rect(0, 0, 12, 10)); // between min and max

        await Assert.That(leaf.Arranged.Width).IsEqualTo(12);
        await Assert.That(fill.Arranged.Width).IsEqualTo(0);
    }

    [Test]
    public async Task WordWrap_DefiniteWidth_GrowsLineCountAsItShrinks()
    {
        var leaf = new MeasuredLeaf(WordWrap);

        await Assert.That(leaf.Measure(new Constraints(20, 100)).Height).IsEqualTo(1);
        await Assert.That(leaf.Measure(new Constraints(8, 100)).Height).IsEqualTo(2);
        await Assert.That(leaf.Measure(new Constraints(4, 100)).Height).IsEqualTo(4);
    }

    [Test]
    public async Task Table_MinAndMaxContent_SumColumnsAndSeatInSolver()
    {
        var leaf = new MeasuredLeaf(Table);
        await Assert.That(leaf.MinContent.Width).IsEqualTo(7);
        await Assert.That(leaf.MaxContent.Width).IsEqualTo(15);
        await Assert.That(leaf.ToTrack().FlexFloor).IsEqualTo(7);
        await Assert.That(leaf.ToTrack().FlexCap).IsEqualTo(15);

        var fill = new StubNode();
        var row = new FlexSplitNode(SplitDir.Horizontal);
        row.AddMeasured(leaf);
        row.Add(fill, FlexTrack.Fill(1));

        row.Arrange(new EngineCells.Rect(0, 0, 30, 4));
        await Assert.That(leaf.Arranged.Width).IsEqualTo(15); // capped at max-content
        await Assert.That(fill.Arranged.Width).IsEqualTo(15);

        row.Arrange(new EngineCells.Rect(0, 0, 7, 4));
        await Assert.That(leaf.Arranged.Width).IsEqualTo(7); // floor at min-content
        await Assert.That(fill.Arranged.Width).IsEqualTo(0);
    }

    [Test]
    public async Task MeasuredLeaf_StableFrame_ZeroAllocation()
    {
        var leaf = new MeasuredLeaf(WordWrap);
        var fill = new StubNode();
        var row = new FlexSplitNode(SplitDir.Horizontal);
        row.AddMeasured(leaf);
        row.Add(fill, FlexTrack.Fill(1));
        var bounds = new Constraints(40, 10);
        var rect = new EngineCells.Rect(0, 0, 40, 10);

        // Warm up the delegate and the solver's stackalloc scratch.
        for (int i = 0; i < 8; i++)
        {
            row.Measure(bounds);
            row.Arrange(rect);
        }

        GC.WaitForPendingFinalizers();
        const int iterations = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            row.Measure(bounds);
            row.Arrange(rect);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0); // thread-scoped: immune to parallel test traffic
    }
}
