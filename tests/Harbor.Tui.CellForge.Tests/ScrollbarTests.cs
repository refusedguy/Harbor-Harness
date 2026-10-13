using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [PRIM3b] render coverage for <see cref="Scrollbar"/>: the ratatui
/// <c>scrollbar.rs</c> thumb formula driven by
/// <see cref="ScrollableViewport"/> geometry. The scrollbar is a pure overlay —
/// hidden when the content fits — so no existing golden changes; these tests
/// lock the thumb vectors and the paint/hide contract instead.
/// </summary>
public class ScrollbarTests
{
    [Test]
    public async Task IsVisible_OnlyWhenContentOverflows()
    {
        await Assert.That(Scrollbar.IsVisible(100, 10)).IsTrue();
        await Assert.That(Scrollbar.IsVisible(10, 10)).IsFalse();
        await Assert.That(Scrollbar.IsVisible(5, 10)).IsFalse();
        await Assert.That(Scrollbar.IsVisible(0, 10)).IsFalse();
        await Assert.That(Scrollbar.IsVisible(0, 0)).IsFalse();
    }

    [Test]
    public async Task ThumbGeometry_ProportionalThumb_FullTravel()
    {
        // total=20, viewport=10, track=10 → 5-row thumb gliding 0..5.
        int[] expectedStarts = [0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5];
        for (int offset = 0; offset < expectedStarts.Length; offset++)
        {
            var (start, length, end) = Scrollbar.ThumbGeometry(20, offset, 10, 10);
            await Assert.That(length).IsEqualTo(5);
            await Assert.That(start).IsEqualTo(expectedStarts[offset]);
            await Assert.That(start + length + end).IsEqualTo(10);
        }
    }

    [Test]
    public async Task ThumbGeometry_SmallViewport_StaysInTrack()
    {
        // total=100, viewport=2, track=5 → single-cell thumb gliding down.
        int[] offsets = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90];
        int[] expectedStarts = [0, 0, 1, 1, 2, 2, 2, 3, 3, 4];
        for (int i = 0; i < offsets.Length; i++)
        {
            var (start, length, end) = Scrollbar.ThumbGeometry(100, offsets[i], 2, 5);
            await Assert.That(length).IsEqualTo(1);
            await Assert.That(start).IsEqualTo(expectedStarts[i]);
            await Assert.That(start + length + end).IsEqualTo(5);
        }
    }

    [Test]
    public async Task ThumbGeometry_PinsToTrackEnds()
    {
        var (topStart, topLen, _) = Scrollbar.ThumbGeometry(100, 0, 10, 10);
        await Assert.That(topStart).IsEqualTo(0);
        await Assert.That(topLen).IsEqualTo(1);

        // Stale offsets clamp: far past the end pins the thumb to the bottom row.
        var (endStart, endLen, endTail) = Scrollbar.ThumbGeometry(100, 10_000, 10, 10);
        await Assert.That(endStart + endLen).IsEqualTo(10);
        await Assert.That(endTail).IsEqualTo(0);

        var (negStart, _, _) = Scrollbar.ThumbGeometry(100, -5, 10, 10);
        await Assert.That(negStart).IsEqualTo(0);
    }

    [Test]
    public async Task ThumbGeometry_HiddenCases_ReturnZero()
    {
        // Content fits (deviation from ratatui fullbar: Harbor hides instead).
        await Assert.That(Scrollbar.ThumbGeometry(5, 0, 10, 10)).IsEqualTo((0, 0, 0));
        await Assert.That(Scrollbar.ThumbGeometry(0, 0, 10, 10)).IsEqualTo((0, 0, 0));
        await Assert.That(Scrollbar.ThumbGeometry(100, 0, 0, 10)).IsEqualTo((0, 0, 0));
        await Assert.That(Scrollbar.ThumbGeometry(100, 0, 10, 0)).IsEqualTo((0, 0, 0));
    }

    [Test]
    public async Task TryPaint_PaintsThumbOverTrack()
    {
        var buffer = new ScreenBuffer(1, 10);
        bool painted = Scrollbar.TryPaint(buffer, new Rect(0, 0, 1, 10), 20, 0, 10);

        await Assert.That(painted).IsTrue();
        // total=20, viewport=10, track=10 → 5-row thumb rows 0..4, track rows 5..9.
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'█');
        await Assert.That(buffer.Get(0, 4).Rune).IsEqualTo((int)'█');
        await Assert.That(buffer.Get(0, 5).Rune).IsEqualTo((int)'│');
        await Assert.That(buffer.Get(0, 9).Rune).IsEqualTo((int)'│');
    }

    [Test]
    public async Task TryPaint_ViewportOverload_FollowsOffset()
    {
        var viewport = new ScrollableViewport();
        viewport.Configure(20, 10);
        viewport.ScrollToEnd();

        var buffer = new ScreenBuffer(1, 10);
        bool painted = Scrollbar.TryPaint(buffer, new Rect(0, 0, 1, 10), viewport);

        await Assert.That(painted).IsTrue();
        // Offset pinned to max=10 → 5-row thumb rows 5..9.
        await Assert.That(buffer.Get(0, 4).Rune).IsEqualTo((int)'│');
        await Assert.That(buffer.Get(0, 5).Rune).IsEqualTo((int)'█');
        await Assert.That(buffer.Get(0, 9).Rune).IsEqualTo((int)'█');
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'│');
    }

    [Test]
    public async Task TryPaint_UsesRightmostColumn()
    {
        var buffer = new ScreenBuffer(3, 10);
        bool painted = Scrollbar.TryPaint(buffer, new Rect(0, 0, 3, 10), 20, 0, 10);

        await Assert.That(painted).IsTrue();
        await Assert.That(buffer.Get(2, 0).Rune).IsEqualTo((int)'█');
        await Assert.That(buffer.Get(2, 9).Rune).IsEqualTo((int)'│');
        await Assert.That(buffer.Get(0, 0)).IsEqualTo(Cell.Blank);
        await Assert.That(buffer.Get(1, 5)).IsEqualTo(Cell.Blank);
    }

    [Test]
    public async Task TryPaint_HiddenLeavesBufferUntouched()
    {
        var buffer = new ScreenBuffer(1, 5);
        bool painted = Scrollbar.TryPaint(buffer, new Rect(0, 0, 1, 5), 3, 0, 10);

        await Assert.That(painted).IsFalse();
        for (int y = 0; y < 5; y++)
        {
            await Assert.That(buffer.Get(0, y)).IsEqualTo(Cell.Blank);
        }
    }

    [Test]
    public async Task Detachable_ScrollInfoOverload_MatchesTriple()
    {
        var info = new ScrollInfo(20, 10, 10);
        await Assert.That(Scrollbar.IsVisible(info)).IsTrue();
        await Assert.That(info.IsScrollable).IsTrue();
        await Assert.That(Scrollbar.ThumbGeometry(info, 10)).IsEqualTo(Scrollbar.ThumbGeometry(20, 10, 10, 10));

        var a = new ScreenBuffer(1, 10);
        var b = new ScreenBuffer(1, 10);
        bool pa = Scrollbar.TryPaint(a, new Rect(0, 0, 1, 10), info);
        bool pb = Scrollbar.TryPaint(b, new Rect(0, 0, 1, 10), 20, 10, 10);

        await Assert.That(pa).IsEqualTo(pb);
        for (int y = 0; y < 10; y++)
        {
            await Assert.That(a.Get(0, y)).IsEqualTo(b.Get(0, y));
        }
    }

    [Test]
    public async Task Detachable_ViewportYieldsTriple()
    {
        var viewport = new ScrollableViewport();
        viewport.Configure(20, 10);
        viewport.SetOffset(4);

        ScrollInfo info = ((IScrollInfoProvider)viewport).GetScrollInfo();
        await Assert.That((info.Total, info.Offset, info.Viewport)).IsEqualTo((20L, 4L, 10));

        var buffer = new ScreenBuffer(1, 10);
        await Assert.That(Scrollbar.TryPaint(buffer, new Rect(0, 0, 1, 10), info)).IsTrue();
    }

    [Test]
    public async Task Detachable_SelectionListUsesSharedIndicator()
    {
        var list = new GenericSelectionList();
        list.SetItems(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"]);
        list.MaxVisibleRows = 4;

        ScrollInfo info = list.GetScrollInfo();
        await Assert.That(info.Total).IsEqualTo(10);
        await Assert.That(info.Viewport).IsEqualTo(4);

        var buffer = new ScreenBuffer(10, 4);
        list.Paint(buffer, new Rect(0, 0, 10, 4));

        // Shared indicator paints on the rightmost column; content stays put.
        await Assert.That(buffer.Get(9, 0).Rune == (int)'█' || buffer.Get(9, 0).Rune == (int)'│').IsTrue();
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'>');
    }
}
