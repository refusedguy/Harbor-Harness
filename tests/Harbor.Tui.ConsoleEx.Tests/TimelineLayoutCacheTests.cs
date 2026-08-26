using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>Counting block: reports how many times Measure was invoked.</summary>
internal sealed class CountingBlock : IChatBlock
{
    private readonly int _lines;
    public int MeasureCalls;
    public int LastWidth = -1;

    public CountingBlock(string kind, int lines, bool exact = true)
    {
        Kind = kind;
        _lines = lines;
        Exact = exact;
    }

    public string Kind { get; }
    public bool Exact { get; }
    public bool IsStreamContinuation => false;
    public int BudgetBytes => 32 + (_lines * 8);

    public BlockMeasure Measure(int width)
    {
        MeasureCalls++;
        LastWidth = width;
        return Exact ? BlockMeasure.Exact(_lines) : BlockMeasure.Estimate(1, _lines);
    }

    public int CheapEstimate(int width) => Exact ? _lines : Math.Max(1, _lines / 2);

    public void Paint(in BlockPaintContext ctx) { }

    public string RawText() => Kind;
}

public class TimelineLayoutCacheTests
{
    [Test]
    public async Task FirstPrepare_IsFullRebuild_AndSettlesOnlyVisible()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 50; i++)
        {
            cache.Append(new CountingBlock($"b{i}", 3));
        }

        // Viewport shows rows 0..10 → blocks 0..3 (3 lines each).
        var outcome = cache.PrepareLayout(width: 40, viewportH: 10, scrollY: 0);

        await Assert.That(outcome).IsEqualTo(LayoutOutcome.FullRebuild);
        await Assert.That(cache.TotalHeight).IsEqualTo(150);
        await Assert.That(cache.MeasureCallsLastFrame).IsLessThanOrEqualTo(5); // settled visible only
        var counting = (CountingBlock)cache.BlockAt(30);
        await Assert.That(counting.MeasureCalls).IsEqualTo(0);                  // far below never touched
    }

    [Test]
    public async Task Append_WhilePinnedBottom_PatchesSuffixWithoutRemasuring()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 20; i++)
        {
            cache.Append(new CountingBlock($"b{i}", 2));
        }

        _ = cache.PrepareLayout(40, 100, 0);

        cache.Append(new CountingBlock("tail", 4));
        var outcome = cache.PrepareLayout(40, 100, scrollY: 34);

        await Assert.That(outcome).IsEqualTo(LayoutOutcome.Patched);
        await Assert.That(cache.TotalHeight).IsEqualTo(44);
        // The appended tail got measured exactly once this frame…
        var newTail = (CountingBlock)cache.BlockAt(20);
        await Assert.That(newTail.MeasureCalls).IsEqualTo(1);
        // …and the frame's measure budget stayed bounded by the suffix.
        await Assert.That(cache.MeasureCallsLastFrame).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task WidthChange_ResetsAndRestoresAnchor()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 30; i++)
        {
            cache.Append(new CountingBlock($"b{i}", 5));
        }

        _ = cache.PrepareLayout(60, 10, scrollY: 40);
        cache.PinAnchor(scrollTopY: 40);

        var outcome = cache.PrepareLayout(30, 10, scrollY: 40);
        await Assert.That(outcome).IsEqualTo(LayoutOutcome.FullRebuild);

        long restored = cache.RestoreAnchor();
        int anchorIdx = cache.EntryAtY(restored);
        await Assert.That(cache.BlockAt(anchorIdx).RawText()).IsEqualTo("b8"); // same block as before
        await Assert.That(restored - cache.BlockTop(anchorIdx)).IsLessThanOrEqualTo(4);

        // All heights were invalidated → every block re-estimated.
        await Assert.That(cache.MeasureCallsLastFrame).IsGreaterThan(0);
    }

    [Test]
    public async Task EntryAtY_MatchesLinearOracle_OnFuzzedTapes()
    {
        var rng = new Random(42);
        for (int trial = 0; trial < 25; trial++)
        {
            var cache = new TimelineLayoutCache();
            var heights = new List<int>();
            for (int i = 0; i < 1 + rng.Next(40); i++)
            {
                int h = 1 + rng.Next(6);
                heights.Add(h);
                cache.Append(new CountingBlock($"x{i}", h));
            }

            _ = cache.PrepareLayout(50, 20, 0);

            long total = cache.TotalHeight;
            for (long y = 0; y < total; y += 3)
            {
                int expected = 0;
                long acc = 0;
                while (acc + heights[expected] <= y && expected < heights.Count - 1)
                {
                    acc += heights[expected];
                    expected++;
                }

                await Assert.That(cache.EntryAtY(y)).IsEqualTo(expected);
            }

            // virtual_y monotonicity property
            for (int i = 1; i < cache.Count; i++)
            {
                await Assert.That(cache.BlockTop(i)).IsGreaterThanOrEqualTo(cache.BlockTop(i - 1));
            }
        }
    }

    [Test]
    public async Task EvictFirst_ShiftsIndicesAndKeepsConsistency()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 10; i++)
        {
            cache.Append(new CountingBlock($"e{i}", 2));
        }

        _ = cache.PrepareLayout(40, 6, 0);
        cache.EvictFirst();
        _ = cache.PrepareLayout(40, 6, 0);

        await Assert.That(cache.Count).IsEqualTo(9);
        await Assert.That(cache.TotalHeight).IsEqualTo(18);
        await Assert.That(cache.BlockAt(0).RawText()).IsEqualTo("e1");
    }

    [Test]
    public async Task Replace_SwapsStreamTailForCommittedBlock()
    {
        var cache = new TimelineLayoutCache();
        cache.Append(new CountingBlock("stream", 7, exact: false));
        _ = cache.PrepareLayout(40, 20, 0);

        var committed = new CountingBlock("assistant", 9);
        cache.Replace(0, committed);
        var outcome = cache.PrepareLayout(40, 20, 0);

        await Assert.That(outcome).IsEqualTo(LayoutOutcome.Patched);
        await Assert.That(cache.BlockAt(0)).IsSameReferenceAs(committed);
        await Assert.That(cache.TotalHeight).IsEqualTo(9);
    }

    [Test]
    public async Task UnchangedCase_RepeatsServeFromCache()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 12; i++)
        {
            cache.Append(new CountingBlock($"c{i}", 3));
        }

        _ = cache.PrepareLayout(40, 9, 3);
        int afterFirst = cache.MeasureCallsLastFrame;
        var second = cache.PrepareLayout(40, 9, 3);
        int afterSecond = cache.MeasureCallsLastFrame;

        await Assert.That(second).IsEqualTo(LayoutOutcome.Unchanged);
        await Assert.That(afterSecond).IsEqualTo(0);
        await Assert.That(afterFirst).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task MarkDirty_PatchesFromGivenIndex()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 8; i++)
        {
            cache.Append(new CountingBlock($"d{i}", 2));
        }

        _ = cache.PrepareLayout(40, 16, 0);
        var tail = (CountingBlock)cache.BlockAt(7);
        int before = tail.MeasureCalls;

        // Stream tail grew: only index 7's height is stale.
        cache.MarkHeightsDirty(7);
        _ = cache.PrepareLayout(40, 16, 14);

        await Assert.That(tail.MeasureCalls).IsGreaterThanOrEqualTo(before);
        await Assert.That(cache.TotalHeight).IsEqualTo(16);
    }
}
