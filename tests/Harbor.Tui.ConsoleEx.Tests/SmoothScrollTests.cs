using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Smooth scroll (HDS v1): user scroll deltas ease toward the target over the
/// micro fade while follow-tail and explicit end snaps stay exact.
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
            tl.EnableSmoothScroll();
        }

        _ = Paint(tl, tick: 0); // first frame → hasPaintedFrame
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
                _ = sb.Append((char)buffer.Get(x, y).Rune);
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

        await Assert.That(tl.EffectiveScrollY).IsEqualTo(tl.ScrollY); // visual == target right away
    }

    [Test]
    public async Task SmoothScroll_GlidesTowardTarget_AndSettles()
    {
        var tl = Populated(smooth: true);
        long tail = tl.TotalHeight - Height;
        tl.ScrollUp(10); // target = tail - 10

        long start = tl.EffectiveScrollY;
        _ = Paint(tl, tick: 11);
        long early = tl.EffectiveScrollY;

        for (long tick = 12; tick < 11 + (PanelFx.FadeFrames / 2); tick++)
        {
            _ = Paint(tl, tick);
        }

        long late = tl.EffectiveScrollY;
        _ = Paint(tl, tick: 10 + PanelFx.FadeFrames + 1);
        long settled = tl.EffectiveScrollY;

        await Assert.That(start).IsEqualTo(tail);        // eased view starts at the pinned position
        await Assert.That(early).IsLessThan(start);      // gliding up (offset decreases)
        await Assert.That(late).IsLessThan(early);       // monotonic ease-out
        await Assert.That(late).IsGreaterThan(tail - 10); // not settled mid-flight
        await Assert.That(settled).IsEqualTo(tail - 10); // lands exactly on target
    }

    [Test]
    public async Task SmoothScroll_PaintDiffersMidFlight_FromSettled()
    {
        var tl = Populated(smooth: true);
        tl.ScrollUp(10);

        var mid = Paint(tl, tick: 11 + (PanelFx.FadeFrames / 2));
        var settled = Paint(tl, tick: 11 + PanelFx.FadeFrames + 1);

        await Assert.That(GridDump.Art(mid)).IsNotEqualTo(GridDump.Art(settled)); // visible glide
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
            await Assert.That(prev - now).IsLessThanOrEqualTo(3); // small per-frame glide
            prev = now;
        }

        tl.ScrollUp(10); // retarget mid-flight
        _ = Paint(tl, tick: 16);
        long after = tl.EffectiveScrollY;

        await Assert.That(prev - after).IsLessThanOrEqualTo(3); // chained, no discontinuity
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

        tl.ScrollDown(4); // unpinned — animates toward tail-6
        _ = Paint(tl, tick: 12 + PanelFx.FadeFrames);
        await Assert.That(tl.EffectiveScrollY).IsNotEqualTo(tl.TotalHeight - Height);

        tl.ScrollToEnd(Height); // follow-tail re-engaged — exact snap
        var buffer = Paint(tl, tick: 13 + PanelFx.FadeFrames);

        await Assert.That(tl.EffectiveScrollY).IsEqualTo(tl.TotalHeight - Height);
        await Assert.That(RowOf(buffer, "line-19")).IsGreaterThanOrEqualTo(0);
    }
}
