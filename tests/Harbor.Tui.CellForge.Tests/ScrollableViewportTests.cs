using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [PRIM3a] core coverage for <see cref="ScrollableViewport"/>: the single
/// <c>max(0, total - viewportH)</c> bounds formula generalized out of
/// <c>TimelineLayoutCache</c> / <see cref="VirtualizedChatTimeline"/>.
/// Timeline behavior itself is unchanged — <see cref="TimelineCache_DelegatesToSameFormula"/>
/// locks the delegation parity.
/// </summary>
public class ScrollableViewportTests
{
    [Test]
    public async Task MaxOffsetFor_SubtractsViewport_ClampedAtZero()
    {
        await Assert.That(ScrollableViewport.MaxOffsetFor(100, 10)).IsEqualTo(90L);
        await Assert.That(ScrollableViewport.MaxOffsetFor(5, 10)).IsEqualTo(0L);
        await Assert.That(ScrollableViewport.MaxOffsetFor(0, 0)).IsEqualTo(0L);
    }

    [Test]
    public async Task MaxOffsetFor_NegativeInputs_TreatedAsZero()
    {
        await Assert.That(ScrollableViewport.MaxOffsetFor(-5, 10)).IsEqualTo(0L);
        await Assert.That(ScrollableViewport.MaxOffsetFor(100, -3)).IsEqualTo(100L);
        await Assert.That(ScrollableViewport.MaxOffsetFor(-5, -3)).IsEqualTo(0L);
    }

    [Test]
    public async Task ClampOffsetFor_PinsToBounds()
    {
        await Assert.That(ScrollableViewport.ClampOffsetFor(-1, 100, 10)).IsEqualTo(0L);
        await Assert.That(ScrollableViewport.ClampOffsetFor(50, 100, 10)).IsEqualTo(50L);
        await Assert.That(ScrollableViewport.ClampOffsetFor(90, 100, 10)).IsEqualTo(90L);
        await Assert.That(ScrollableViewport.ClampOffsetFor(1000, 100, 10)).IsEqualTo(90L);
        await Assert.That(ScrollableViewport.ClampOffsetFor(7, 5, 10)).IsEqualTo(0L);
    }

    [Test]
    public async Task SetOffset_And_ScrollBy_Clamp()
    {
        var viewport = new ScrollableViewport();
        viewport.Configure(100, 10);

        await Assert.That(viewport.SetOffset(50)).IsEqualTo(50L);
        await Assert.That(viewport.Offset).IsEqualTo(50L);
        await Assert.That(viewport.AtTop).IsFalse();
        await Assert.That(viewport.AtBottom).IsFalse();

        await Assert.That(viewport.ScrollBy(100)).IsEqualTo(90L);
        await Assert.That(viewport.AtBottom).IsTrue();

        await Assert.That(viewport.ScrollBy(-200)).IsEqualTo(0L);
        await Assert.That(viewport.AtTop).IsTrue();
    }

    [Test]
    public async Task Resize_And_SetTotal_ReclampOffset()
    {
        var viewport = new ScrollableViewport();
        viewport.Configure(100, 10);
        viewport.SetOffset(90);

        viewport.Resize(100); // max collapses to 0
        await Assert.That(viewport.Offset).IsEqualTo(0L);

        viewport.Configure(100, 10);
        viewport.SetOffset(90);
        viewport.SetTotal(5); // max collapses to 0
        await Assert.That(viewport.Offset).IsEqualTo(0L);

        viewport.SetTotal(50); // growing never moves a valid offset
        await Assert.That(viewport.Offset).IsEqualTo(0L);
        await Assert.That(viewport.MaxOffset).IsEqualTo(40L);
    }

    [Test]
    public async Task ScrollToTop_And_ScrollToEnd_Pin()
    {
        var viewport = new ScrollableViewport();
        viewport.Configure(100, 10);

        viewport.ScrollToEnd();
        await Assert.That(viewport.Offset).IsEqualTo(90L);
        await Assert.That(viewport.AtBottom).IsTrue();

        viewport.ScrollToTop();
        await Assert.That(viewport.Offset).IsEqualTo(0L);
        await Assert.That(viewport.AtTop).IsTrue();
    }

    [Test]
    public async Task VisibleSlice_IsClippedWindow()
    {
        var viewport = new ScrollableViewport();

        var (emptyFirst, emptyLast) = viewport.VisibleSlice();
        await Assert.That(emptyFirst).IsEqualTo(0L);
        await Assert.That(emptyLast).IsEqualTo(0L);
        await Assert.That(viewport.VisibleCount).IsEqualTo(0L);

        viewport.Configure(100, 10);
        viewport.SetOffset(5);
        var (first, last) = viewport.VisibleSlice();
        await Assert.That(first).IsEqualTo(5L);
        await Assert.That(last).IsEqualTo(15L);
        await Assert.That(viewport.VisibleCount).IsEqualTo(10L);

        viewport.ScrollToEnd();
        var (tailFirst, tailLast) = viewport.VisibleSlice();
        await Assert.That(tailFirst).IsEqualTo(90L);
        await Assert.That(tailLast).IsEqualTo(100L);

        viewport.Configure(5, 10); // viewport taller than content
        var (wideFirst, wideLast) = viewport.VisibleSlice();
        await Assert.That(wideFirst).IsEqualTo(0L);
        await Assert.That(wideLast).IsEqualTo(5L);
        await Assert.That(viewport.VisibleCount).IsEqualTo(5L);
    }

    [Test]
    public async Task TimelineCache_DelegatesToSameFormula()
    {
        var cache = new TimelineLayoutCache();
        for (int i = 0; i < 10; i++)
        {
            cache.Append(new CountingBlock($"v{i}", 3));
        }

        _ = cache.PrepareLayout(width: 40, viewportH: 10, scrollY: 0);
        await Assert.That(cache.TotalHeight).IsEqualTo(30L);

        int[] heights = [0, 1, 10, 29, 30, 100];
        foreach (int h in heights)
        {
            await Assert.That(cache.MaxScrollFor(h))
                .IsEqualTo(ScrollableViewport.MaxOffsetFor(cache.TotalHeight, h));
        }

        long[] offsets = [-5, 0, 15, 29, 30, 100];
        foreach (long y in offsets)
        {
            await Assert.That(cache.ClampScrollY(y, 10))
                .IsEqualTo(ScrollableViewport.ClampOffsetFor(y, cache.TotalHeight, 10));
        }
    }
}
