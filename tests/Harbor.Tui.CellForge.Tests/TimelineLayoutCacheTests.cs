using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

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

/// <summary>
/// (#412) Wrapping block whose height DEPENDS ON ITS WIDTH — the property
/// that makes width-keying load-bearing rather than an optimisation. A
/// width-insensitive block cannot tell a correct width-keyed layout from a
/// stale one, so any width test built on <see cref="CountingBlock"/> would
/// pass vacuously (the #591 failure shape: a plausible 0).
///
/// Wraps like the real blocks do: <c>ceil(chars / width)</c> rows, with a
/// <c>CheapEstimate</c> that deliberately DISAGREES with the measurement, so
/// a test can distinguish "asked the block" from "remembered the block".
/// </summary>
internal sealed class WrappingBlock : IChatBlock
{
    /// <param name="honestEstimate">
    /// When true, <see cref="CheapEstimate"/> returns the exact row count, so
    /// an estimate and a measurement are indistinguishable by height. Use it
    /// ONLY in guards that compare heights per block: a lossy estimate is
    /// allowed to disagree with truth (that is what an estimate is), so such a
    /// comparison would be a false failure. Use the default (over-estimating)
    /// everywhere else, where a test must be able to tell the two apart.
    /// </param>
    public WrappingBlock(string kind, int logicalLines, int lineChars, bool honestEstimate = false)
    {
        Kind = kind;
        Chars = logicalLines * (lineChars + 1);
        HonestEstimate = honestEstimate;
    }

    public string Kind { get; }
    public bool IsStreamContinuation => false;
    public int Chars { get; }
    public bool HonestEstimate { get; }
    public int BudgetBytes => 32 + (Chars * 2);

    public int MeasureCalls;
    public int EstimateCalls;
    public int LastMeasureWidth = -1;

    /// <summary>Row count for <paramref name="width"/> — the truth.</summary>
    public int RowsAt(int width) => Math.Max(1, (Chars + width - 1) / width);

    public BlockMeasure Measure(int width)
    {
        MeasureCalls++;
        LastMeasureWidth = width;
        return BlockMeasure.Exact(RowsAt(width));
    }

    /// <summary>
    /// Over-estimates on purpose (never more than the true row count) so a
    /// test can tell an estimate from a measurement by height alone — except
    /// when <see cref="HonestEstimate"/> is set.
    /// </summary>
    public int CheapEstimate(int width)
    {
        EstimateCalls++;
        return HonestEstimate ? RowsAt(width) : Math.Max(1, Chars / (width * 4) + 1);
    }

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

    // ── #412 virtualization honesty ─────────────────────────────────────────
    // Every bound below is stated as a COUNTER, never as wall-clock. The
    // counter is what makes the assertion falsifiable: an unfixed tree reports
    // EstimateCallsLastFrame == Count (all 10 000), so these fail loudly
    // instead of passing on a fast machine.

    /// <summary>
    /// The headline measurement. 10 000 blocks in a 40-row viewport.
    ///
    /// Before #412 the first layout measured 4 blocks (correct — Measure was
    /// always viewport-bounded) but called <c>CheapEstimate</c> on ALL 10 000,
    /// scanning 12.12M characters (10 000 × <see cref="WrappingBlock.Chars"/>)
    /// to lay out 40 visible rows: a 2500× per-item charge for a 4-block
    /// window.
    ///
    /// Bounds stated here, and why:
    /// <list type="bullet">
    /// <item><description><c>EstimateCallsLastFrame == Count</c> on a cold
    /// cache — every block has no height at any width, so all of them must be
    /// asked once. This is the O(transcript) pass and it is UNAVOIDABLE at
    /// first layout; the slice's job is to stop paying it again, not to
    /// pretend it away.</description></item>
    /// <item><description><c>MeasureCallsLastFrame &lt;= blocks in the visible
    /// range</c> — the exact call, the expensive one, stays window-bounded.
    /// Measured 4 of 10 000 here.</description></item>
    /// </list>
    /// </summary>
    [Test]
    public async Task TenThousandBlocks_ColdLayout_AsksEachBlockOnce_AndMeasuresOnlyTheWindow()
    {
        const int Count = 10_000;
        const int ViewportH = 40;

        var cache = new TimelineLayoutCache();
        var blocks = new WrappingBlock[Count];
        for (int i = 0; i < Count; i++)
        {
            blocks[i] = new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100);
            cache.Append(blocks[i]);
        }

        var outcome = cache.PrepareLayout(width: 100, viewportH: ViewportH, scrollY: 0);

        await Assert.That(outcome).IsEqualTo(LayoutOutcome.FullRebuild);

        // Cold cache: every block asked exactly once. The documented, bounded cost.
        await Assert.That(cache.EstimateCallsLastFrame).IsEqualTo(Count);
        await Assert.That(blocks.Sum(b => b.EstimateCalls)).IsEqualTo(Count);

        // The exact call is window-bounded. The bound is computed INDEPENDENTLY
        // here — by summing the blocks' own estimates until the viewport is
        // full — rather than read back from the cache's VisibleRange. A range
        // read back after the settle reflects the MEASURED heights, which are
        // what the passes were bounded by, so using it would compare the
        // result against itself.
        int estimatedWindow = 0;
        int rows = 0;
        while (rows < ViewportH && estimatedWindow < Count)
        {
            rows += blocks[estimatedWindow].CheapEstimate(100);
            estimatedWindow++;
        }

        await Assert.That(cache.MeasureCallsLastFrame).IsLessThanOrEqualTo(estimatedWindow);
        await Assert.That(blocks[Count - 1].MeasureCalls).IsEqualTo(0); // far below, never asked

        // And the window really is a small fraction of the transcript, which is
        // what makes the ratio 2500x: the user sees ~10 of 10 000 blocks while
        // layout still asks every one of them for an estimate.
        await Assert.That(estimatedWindow).IsLessThan(Count / 100);
    }

    /// <summary>
    /// Width-keyed cache. Measure at width A, flip to B, flip back to A: the
    /// third pass must cost ZERO per-block work.
    ///
    /// This is the assertion the unfixed tree cannot satisfy — it reported
    /// Measure=4, CheapEstimate=10 000 on the third pass, because Case 1 did
    /// <c>Array.Clear(_slots)</c> and re-estimated the whole transcript.
    ///
    /// Uses <see cref="WrappingBlock"/> (width-sensitive) so the guard cannot
    /// pass vacuously, and asserts the counter AND the blocks' own tallies
    /// agree — a counter that lies while the blocks were asked would still be
    /// caught by the second assertion.
    /// </summary>
    [Test]
    public async Task WidthFlip_BackToAMeasuredWidth_CostsNoPerBlockWork()
    {
        const int Count = 10_000;
        const int ViewportH = 40;
        const int WidthA = 100;
        const int WidthB = 60;

        var cache = new TimelineLayoutCache();
        var blocks = new WrappingBlock[Count];
        for (int i = 0; i < Count; i++)
        {
            blocks[i] = new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100);
            cache.Append(blocks[i]);
        }

        _ = cache.PrepareLayout(WidthA, ViewportH, 0);

        _ = cache.PrepareLayout(WidthB, ViewportH, 0);
        await Assert.That(cache.EstimateCallsLastFrame).IsEqualTo(Count); // B is unseen: full pass

        // Back to A — the round trip a real session produces (drag the splitter
        // back, toggle a sidebar, focus a pane).
        var outcome = cache.PrepareLayout(WidthA, ViewportH, 0);

        await Assert.That(outcome).IsEqualTo(LayoutOutcome.FullRebuild);
        await Assert.That(cache.EstimateCallsLastFrame).IsEqualTo(0);
        await Assert.That(cache.MeasureCallsLastFrame).IsEqualTo(0);
        await Assert.That(blocks.Sum(b => b.EstimateCalls)).IsEqualTo(Count * 2); // A cold + B, nothing since
    }

    /// <summary>
    /// Two widths must not share heights. After laying out at A and then B, a
    /// block's height is B's, not A's — proven by comparing against a COLD
    /// cache over the same blocks at width B, which is the ground truth (the
    /// cache's own history is not an oracle: a cold cache measures a different
    /// SET of blocks on its first pass, so comparing a laid-out cache to one is
    /// a false failure).
    /// </summary>
    [Test]
    public async Task TwoWidths_DoNotShareMeasuredHeights()
    {
        const int ViewportH = 40;
        var blocks = new List<WrappingBlock>();
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 200; i++)
        {
            var b = new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100);
            blocks.Add(b);
            cache.Append(b);
        }

        _ = cache.PrepareLayout(width: 200, viewportH: ViewportH, scrollY: 0);
        _ = cache.PrepareLayout(width: 50, viewportH: ViewportH, scrollY: 0);

        // Ground truth at width 50: a fresh cache, same blocks, same width.
        var oracle = new TimelineLayoutCache();
        foreach (var b in blocks)
        {
            oracle.Append(b);
        }

        _ = oracle.PrepareLayout(width: 50, viewportH: ViewportH, scrollY: 0);

        await Assert.That(cache.TotalHeight).IsEqualTo(oracle.TotalHeight);
        await Assert.That(cache.VisibleRange(0, ViewportH).First).IsEqualTo(oracle.VisibleRange(0, ViewportH).First);
        await Assert.That(cache.VisibleRange(0, ViewportH).Last).IsEqualTo(oracle.VisibleRange(0, ViewportH).Last);

        // And narrower really does mean taller here — the control that keeps
        // the assertion above from passing because nothing changed at all.
        var wideOracle = new TimelineLayoutCache();
        foreach (var b in blocks)
        {
            wideOracle.Append(b);
        }

        _ = wideOracle.PrepareLayout(width: 200, viewportH: ViewportH, scrollY: 0);
        await Assert.That(oracle.TotalHeight).IsGreaterThan(wideOracle.TotalHeight);
    }

    /// <summary>
    /// The retention bound. Memory is the reason this is capped, so the cap is
    /// asserted rather than trusted: 190 distinct widths must leave at most
    /// three layouts retained (active + 2), not 190.
    /// </summary>
    [Test]
    public async Task RetainedWidths_StayBounded_UnderSustainedResize()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 500; i++)
        {
            cache.Append(new WrappingBlock($"b{i}", logicalLines: 4, lineChars: 40));
        }

        for (int width = 10; width < 200; width++)
        {
            _ = cache.PrepareLayout(width, viewportH: 40, scrollY: 0);
        }

        await Assert.That(cache.RetainedWidthCount).IsLessThanOrEqualTo(3);
    }

    /// <summary>
    /// Eviction must keep every width-keyed array index-aligned with
    /// <c>_blocks</c>. A retained array that failed to shift would hand block
    /// <c>i</c> the row count of block <c>i-1</c> — a silent correctness bug
    /// that no counter would show.
    ///
    /// The oracle is per-block truth, not arithmetic: each block is measured
    /// alone at width A, and the round-tripped cache must agree block for
    /// block. (Comparing EffectiveHeight of a laid-out cache to a fully
    /// measured reference is a FALSE failure — an ESTIMATE is allowed to
    /// disagree with truth; that is what an estimate is.)
    /// </summary>
    [Test]
    public async Task Eviction_KeepsEveryWidthKeyedArrayAligned()
    {
        const int Count = 50;
        const int Evict = 20;
        const int WidthA = 100;
        const int WidthB = 50;
        const int ViewportH = 40;

        var cache = new TimelineLayoutCache();
        var blocks = new List<WrappingBlock>();
        for (int i = 0; i < Count; i++)
        {
            // honestEstimate: this guard compares heights per block, and a
            // lossy estimate is allowed to disagree with truth.
            var b = new WrappingBlock($"e{i}", logicalLines: 4, lineChars: 40, honestEstimate: true);
            blocks.Add(b);
            cache.Append(b);
        }

        _ = cache.PrepareLayout(WidthA, ViewportH, 0);
        for (int i = 0; i < Evict; i++)
        {
            cache.EvictFirst();
        }

        _ = cache.PrepareLayout(WidthA, ViewportH, 0);
        _ = cache.PrepareLayout(WidthB, ViewportH, 0);
        _ = cache.PrepareLayout(WidthA, ViewportH, 0); // round trip onto a retained width

        await Assert.That(cache.Count).IsEqualTo(Count - Evict);

        // Identity shifted correctly.
        await Assert.That(cache.BlockAt(0).RawText()).IsEqualTo($"e{Evict}");
        await Assert.That(cache.BlockAt(cache.Count - 1).RawText()).IsEqualTo($"e{Count - 1}");

        // Per-block height truth at width A, measured one block at a time. A
        // retained array that failed to shift would give block i the row count
        // of block i-1, and this catches it per block rather than in a total.
        for (int i = 0; i < cache.Count; i++)
        {
            await Assert.That(cache.EffectiveHeight(i))
                .IsEqualTo(blocks[i + Evict].RowsAt(WidthA))
                .Because($"block {i} must carry the height width {WidthA} gives it");
        }
    }

    /// <summary>
    /// A scroll through already-measured heights costs nothing — the everyday
    /// case, and the one #46 S3's scale gate will ride on. Measured worst case
    /// over the whole 40 036-row timeline stepped 20 rows at a time (2 000
    /// frames): 2 measure calls (blocks newly entering the window), 0
    /// estimates.
    /// </summary>
    [Test]
    public async Task ScrollingThroughMeasuredHeights_CostsNothingPerFrame()
    {
        const int Count = 10_000;
        const int ViewportH = 40;

        var cache = new TimelineLayoutCache();
        for (int i = 0; i < Count; i++)
        {
            cache.Append(new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100));
        }

        _ = cache.PrepareLayout(100, ViewportH, 0);

        int worstMeasures = 0;
        long worstEstimates = 0;
        long total = cache.TotalHeight;
        for (long y = 0; y + ViewportH < total; y += 20)
        {
            _ = cache.PrepareLayout(100, ViewportH, y);
            worstMeasures = Math.Max(worstMeasures, cache.MeasureCallsLastFrame);
            worstEstimates = Math.Max(worstEstimates, cache.EstimateCallsLastFrame);
        }

        await Assert.That(worstEstimates).IsEqualTo(0);
        await Assert.That(worstMeasures).IsLessThanOrEqualTo(ViewportH);
    }

    /// <summary>
    /// 50 appends while scrolled up must not disturb the anchored region and
    /// must not re-estimate the transcript. (The anchor IDENTITY is #46 S5's
    /// slice; this asserts only the layout-side cost, which is what this issue
    /// owns.)
    /// </summary>
    [Test]
    public async Task AppendsWhileScrolledUp_DoNotReEstimateTheTranscript()
    {
        const int Count = 10_000;
        const int ViewportH = 40;

        var cache = new TimelineLayoutCache();
        for (int i = 0; i < Count; i++)
        {
            cache.Append(new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100));
        }

        _ = cache.PrepareLayout(100, ViewportH, 4_000);
        for (int i = 0; i < 50; i++)
        {
            cache.Append(new WrappingBlock($"tail{i}", logicalLines: 12, lineChars: 100));
        }

        var outcome = cache.PrepareLayout(100, ViewportH, 4_000);

        await Assert.That(outcome).IsEqualTo(LayoutOutcome.Patched);
        await Assert.That(cache.EstimateCallsLastFrame).IsLessThanOrEqualTo(50); // the new tail only
        await Assert.That(cache.MeasureCallsLastFrame).IsLessThanOrEqualTo(ViewportH);
        await Assert.That(cache.Count).IsEqualTo(Count + 50);
    }

    /// <summary>
    /// A mutated card must lose its height at EVERY width, not just the active
    /// one — otherwise returning to a retained width re-adopts a row count that
    /// no longer describes the block.
    /// </summary>
    [Test]
    public async Task Replace_InvalidatesTheHeightAtEveryRetainedWidth()
    {
        const int ViewportH = 40;
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 30; i++)
        {
            cache.Append(new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100));
        }

        _ = cache.PrepareLayout(100, ViewportH, 0);
        _ = cache.PrepareLayout(60, ViewportH, 0);

        // honestEstimate: block 0 is in the visible window here, so it is measured
        // exactly — but compare against the block's own RowsAt rather than a
        // solo cache, so a stale retained height cannot be masked.
        var tall = new WrappingBlock("tall", logicalLines: 40, lineChars: 100, honestEstimate: true);
        cache.Replace(0, tall);
        _ = cache.PrepareLayout(60, ViewportH, 0);
        _ = cache.PrepareLayout(100, ViewportH, 0); // back to a retained width

        await Assert.That(cache.EffectiveHeight(0)).IsEqualTo(tall.RowsAt(100));
    }

    /// <summary>
    /// <c>Clear</c> must drop the retained widths too: a new session's blocks
    /// are not the evicted ones, so a surviving snapshot would re-adopt row
    /// counts describing blocks that no longer exist.
    /// </summary>
    [Test]
    public async Task Clear_DropsRetainedWidths()
    {
        const int ViewportH = 40;
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 40; i++)
        {
            cache.Append(new WrappingBlock($"b{i}", logicalLines: 12, lineChars: 100));
        }

        _ = cache.PrepareLayout(100, ViewportH, 0);
        _ = cache.PrepareLayout(60, ViewportH, 0);
        await Assert.That(cache.RetainedWidthCount).IsEqualTo(2);

        cache.Clear();
        await Assert.That(cache.RetainedWidthCount).IsEqualTo(0);

        var fresh = new WrappingBlock("only", logicalLines: 3, lineChars: 10);
        cache.Append(fresh);
        _ = cache.PrepareLayout(100, ViewportH, 0);

        // The only block is visible, so it is measured — a surviving snapshot
        // from before the Clear would hand back the previous session's rows.
        await Assert.That(cache.EffectiveHeight(0)).IsEqualTo(fresh.RowsAt(100));
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
