using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2521: <see cref="FlexSplitNode"/> keeps the frames produced by the last
/// solve and reuses them when the normalized Rect, the track list and the
/// <c>Auto</c> intrinsics are unchanged. <see cref="FlexSplitNode.Hits"/> /
/// <see cref="FlexSplitNode.Misses"/> are the counters: a hit means the solver
/// did not run again. Layout math only — no painting, no viewport, no goldens
/// touched.
/// </summary>
public sealed class FlexSplitNodeLayoutCacheTests
{
    private sealed class SpyNode : EngineCells.INode
    {
        public int Arranges { get; private set; }

        public EngineCells.Size Want { get; set; } = new(4, 4);

        public EngineCells.Rect Arranged { get; private set; }

        public EngineCells.Size Measure(Constraints constraints) => Want;

        public void Arrange(EngineCells.Rect rect)
        {
            Arranges++;
            Arranged = rect;
        }

        public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
        }
    }

    private static FlexSplitNode RowWith(SpyNode a, SpyNode b)
    {
        var split = new FlexSplitNode(SplitDir.Horizontal);
        split.Add(a, FlexTrack.Fixed(6));
        split.Add(b, FlexTrack.Fill(1));
        return split;
    }

    [Test]
    public async Task Arrange_SameRectAndTracks_HitsCacheAndSkipsSolver()
    {
        var a = new SpyNode();
        var b = new SpyNode();
        var split = RowWith(a, b);
        var rect = new EngineCells.Rect(0, 0, 20, 4);

        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(0);
        await Assert.That(split.Misses).IsEqualTo(1);
        await Assert.That(a.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 6, 4));
        await Assert.That(b.Arranged).IsEqualTo(new EngineCells.Rect(6, 0, 14, 4));

        int aArranges = a.Arranges;
        int bArranges = b.Arranges;
        split.Arrange(rect);

        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(1); // solver not re-run
        await Assert.That(split.Arranged).IsEqualTo(rect);
        await Assert.That(a.Arranges).IsEqualTo(aArranges + 1); // frames still forwarded
        await Assert.That(b.Arranges).IsEqualTo(bArranges + 1);
        await Assert.That(a.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 6, 4));
        await Assert.That(b.Arranged).IsEqualTo(new EngineCells.Rect(6, 0, 14, 4));
    }

    [Test]
    public async Task Arrange_ChangedRect_InvalidatesCache()
    {
        var a = new SpyNode();
        var b = new SpyNode();
        var split = RowWith(a, b);
        var first = new EngineCells.Rect(0, 0, 20, 4);
        var second = new EngineCells.Rect(0, 0, 30, 4);

        split.Arrange(first);
        split.Arrange(first);
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(1);

        split.Arrange(second); // different Rect must be a miss
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(2);
        await Assert.That(b.Arranged).IsEqualTo(new EngineCells.Rect(6, 0, 24, 4));

        split.Arrange(second); // same Rect again must hit the NEW entry
        await Assert.That(split.Hits).IsEqualTo(2);
        await Assert.That(split.Misses).IsEqualTo(2);
    }

    [Test]
    public async Task Add_InvalidatesCache()
    {
        var a = new SpyNode();
        var b = new SpyNode();
        var c = new SpyNode();
        var split = RowWith(a, b);
        var rect = new EngineCells.Rect(0, 0, 20, 4);

        split.Arrange(rect);
        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(1);

        split.Add(c, FlexTrack.Fixed(2));
        split.Arrange(rect); // new track list must be a miss
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(2);
        await Assert.That(b.Arranged).IsEqualTo(new EngineCells.Rect(6, 0, 12, 4));
        await Assert.That(c.Arranged).IsEqualTo(new EngineCells.Rect(18, 0, 2, 4));

        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(2);
        await Assert.That(split.Misses).IsEqualTo(2);
    }

    [Test]
    public async Task Remove_InvalidatesCache()
    {
        var a = new SpyNode();
        var b = new SpyNode();
        var c = new SpyNode();
        var split = RowWith(a, b);
        split.Add(c, FlexTrack.Fixed(2));
        var rect = new EngineCells.Rect(0, 0, 20, 4);

        split.Arrange(rect);
        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(1);

        await Assert.That(split.Remove(a)).IsTrue();
        split.Arrange(rect); // new track list must be a miss
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(2);
        await Assert.That(b.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 18, 4));
        await Assert.That(c.Arranged).IsEqualTo(new EngineCells.Rect(18, 0, 2, 4));

        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(2);
        await Assert.That(split.Misses).IsEqualTo(2);
    }

    [Test]
    public async Task AutoIntrinsicChange_InvalidatesCache()
    {
        var auto = new SpyNode { Want = new EngineCells.Size(4, 4) };
        var fill = new SpyNode();
        var split = new FlexSplitNode(SplitDir.Horizontal);
        split.Add(auto, FlexTrack.Auto());
        split.Add(fill, FlexTrack.Fill(1));
        var rect = new EngineCells.Rect(0, 0, 20, 4);

        split.Arrange(rect);
        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(1);
        await Assert.That(auto.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 4, 4));

        auto.Want = new EngineCells.Size(9, 4); // content changed, tracks did not
        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(2);
        await Assert.That(auto.Arranged).IsEqualTo(new EngineCells.Rect(0, 0, 9, 4));
        await Assert.That(fill.Arranged).IsEqualTo(new EngineCells.Rect(9, 0, 11, 4));

        split.Arrange(rect);
        await Assert.That(split.Hits).IsEqualTo(2);
        await Assert.That(split.Misses).IsEqualTo(2);
    }

    [Test]
    public async Task NormalizedRect_EquivalentNegativeExtents_StillHitCache()
    {
        var a = new SpyNode();
        var b = new SpyNode();
        var split = RowWith(a, b);

        // Both raw rects normalize to (0, 0, 0, 4); the cache key is normalized.
        split.Arrange(new EngineCells.Rect(0, 0, -1, 4));
        split.Arrange(new EngineCells.Rect(0, 0, -2, 4));

        await Assert.That(split.Hits).IsEqualTo(1);
        await Assert.That(split.Misses).IsEqualTo(1);
        await Assert.That(b.Arranged.Width).IsEqualTo(0);
    }
}
