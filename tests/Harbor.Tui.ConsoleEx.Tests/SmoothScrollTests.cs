using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Smooth scroll (HDS v1): user scroll deltas ease toward the target over the
/// micro fade while follow-tail and explicit end/top snaps stay exact.
/// Deterministic ticks — no wall clock.
/// </summary>
public class SmoothScrollTests
{
    private const int Width = 40;
    private const int Height = 6;

    private static VirtualizedChatTimeline Populated(bool smooth)
    {
        var tl = new VirtualizedChatTimeline();
        if (smooth)
        {
            tl.EnableEntranceFx();
            tl.EnableSmoothScroll();
        }

        _ = Paint(tl, tick: 0); // first frame → hasPaintedFrame, entrance FX settle
        for (int i = 0; i < 20; i++)
        {
            tl.Append(new UserBlock($"line-{i:00}"));
        }

        tl.ScrollToEnd(Height);
        _ = Paint(tl, tick: 10); // settled at the tail
        return tl;
    }

    private static ScreenBuffer Paint(VirtualizedChatTimeline tl, long tick)
    {
        tl.CurrentTick = tick;
        _ = tl.PrepareFrame(Width, Height);
        var buffer = new ScreenBuffer(Width, Height);
        tl.Paint(buffer, new Rect(0, 0, Width, Height));
        return buffer;
    }

    private static int RowOf(ScreenBuffer buffer, string needle)
    {
        for (int y = 0; y < buffer.Rows; y++)
        {
            var sb = new System.Text.StringBuilder();
            for (int x = 0; x < buffer.Cols; x++)
            {
                _ = sb.Append(buffer.Get(x, y).Ch);
            }

            if (sb.ToString().Contains(needle, StringComparison.Ordinal))
            {
                return y;
            }
        }

        return -1;
    }

    [Test]
    public async Task DisabledByDefault_ScrollJumpsImmediately()
    {
        var tl = Populated(smooth: false);
        tl.ScrollUp(10);
        _ = Paint(tl, tick: 11);

        await Assert.That(tl.EffectiveScrollY).IsEqualTo(tl.ScrollY);
        await Assert.That(tl.EffectiveScrollY).IsEqualTo(tl.TotalHeight - Height);
    }

    [Test]
    public async Task SmoothScroll_GlidesTowardTarget_AndSettles()
    {
        var tl = Populated(smooth: true);
        long target = tl.TotalHeight - Height;
        tl.ScrollUp(10);

        long start = tl.EffectiveScrollY;
        _ = Paint(tl, tick: 11);
        long early = tl.EffectiveScrollY;

        for (long tick = 12; tick < 10 + PanelFx.FadeFrames; tick++)
        {
            _ = Paint(tl, tick);
        }

        long late = tl.EffectiveScrollY;
        _ = Paint(tl, tick: 10 + PanelFx.FadeFrames + 1);
        long settled = tl.EffectiveScrollY;

        await Assert.That(start).IsEqualTo(target);            // pinned at the tail
        await Assert.That(early).IsEqualTo(start);             // ease starts from the current view
        await Assert.That(late).IsGreaterThan(early);          // moving toward the target (up = larger offset)
        await Assert.That(late).IsLessThan(target);            // not there yet mid-flight
        await Assert.That(settled).IsEqualTo(target);          // settles exactly on target
    }

    [Test]
    public async Task SmoothScroll_PaintShiftsContentProgressively()
    {
        var tl = Populated(smooth: true);
        tl.ScrollUp(10);

        var buffer = Paint(tl, tick: 11 + (PanelFx.FadeFrames / 2));
        int midRow = RowOf(buffer, "line-19");

        buffer = Paint(tl, tick: 11 + PanelFx.FadeFrames + 1);
        int settledRow = RowOf(buffer, "line-19");

        await Assert.That(midRow).IsGreaterThanOrEqualTo(0);
        await Assert.That(settledRow).IsGreaterThanOrEqualTo(0);
        await Assert.That(midRow).IsNotEqualTo(settledRow); // visible glide, not a jump
    }

    [Test]
    public async Task ChainedScrolls_RetargetWithoutJump()
    {
        var tl = Populated(smooth: true);
        tl.ScrollUp(10);

        _ = Paint(tl, tick: 11);
        long prev = tl.EffectiveScrollY;
        for (long tick = 12; tick <= 15; tick++)
        {
            _ = Paint(tl, tick);
            long now = tl.EffectiveScrollY;
            await Assert.That(Math.Abs(now - prev)).IsLessThanOrEqualTo(3); // per-frame glide, small steps
            prev = now;
        }

        tl.ScrollUp(10); // retarget mid-flight
        _ = Paint(tl, tick: 16);
        long after = tl.EffectiveScrollY;

        await Assert.That(after).IsGreaterThanOrEqualTo(prev);   // keeps moving toward the new target
        await Assert.That(after - prev).IsLessThanOrEqualTo(3);  // chained, no discontinuity
    }

    [Test]
    public async Task FollowTailScroll_SnapsImmediately()
    {
        var tl = Populated(smooth: true);
        tl.ScrollUp(10);
        for (long tick = 11; tick < 11 + PanelFx.FadeFrames; tick++)
        {
            _ = Paint(tl, tick);
        }

        tl.ScrollDown(4); // still unpinned — animates
        _ = Paint(tl, tick: 12 + PanelFx.FadeFrames);

        tl.ScrollToEnd(Height); // follow-tail re-engaged — must be exact
        var buffer = Paint(tl, tick: 13 + PanelFx.FadeFrames);

        await Assert.That(tl.EffectiveScrollY).IsEqualTo(tl.TotalHeight - Height);
        await Assert.That(RowOf(buffer, "line-19")).IsGreaterThanOrEqualTo(0);
    }
}
