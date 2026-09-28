using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Tests;

/// <summary>ENG7 (issue #278): 60 fps ticker gate over the wake-driven frame loop.</summary>
public class FrameTickerTests
{
    [Test]
    public async Task Cap_Is_SixtyFps()
    {
        await Assert.That(FrameTicker.MaxHertz).IsEqualTo(60);
        await Assert.That(FrameTicker.MinInterval).IsEqualTo(TimeSpan.FromMilliseconds(1000.0 / 60));
    }

    [Test]
    public async Task First_Frame_Is_Always_Due()
    {
        var ticker = new FrameTicker();
        await Assert.That(ticker.ShouldRender(0)).IsTrue();
        await Assert.That(ticker.MsUntilDue(0)).IsEqualTo(0);
    }

    [Test]
    public async Task Burst_Coalesces_Until_Interval_Elapses()
    {
        var ticker = new FrameTicker();
        ticker.MarkRendered(1000);

        await Assert.That(ticker.ShouldRender(1000)).IsFalse();
        await Assert.That(ticker.ShouldRender(1015)).IsFalse();
        await Assert.That(ticker.MsUntilDue(1010)).IsEqualTo(6);
        await Assert.That(ticker.ShouldRender(1016)).IsTrue();
        await Assert.That(ticker.MsUntilDue(1016)).IsEqualTo(0);
        await Assert.That(ticker.MsUntilDue(2000)).IsEqualTo(0);
    }

    [Test]
    public async Task Counters_Track_Render_Suppress_Pace()
    {
        var ticker = new FrameTicker();
        ticker.MarkRendered(0);
        ticker.MarkSuppressed();
        ticker.MarkSuppressed();
        ticker.MarkPaced();

        await Assert.That(ticker.RenderedFrames).IsEqualTo(1);
        await Assert.That(ticker.SuppressedFrames).IsEqualTo(2);
        await Assert.That(ticker.PacedFrames).IsEqualTo(1);
    }

    [Test]
    public async Task SixtyHertz_Burst_Yields_One_Frame_Per_Interval()
    {
        var ticker = new FrameTicker();
        int rendered = 0;
        for (long t = 0; t < 100; t++)
        {
            if (ticker.ShouldRender(t))
            {
                ticker.MarkRendered(t);
                rendered++;
            }
            else
            {
                ticker.MarkSuppressed();
            }
        }

        // 100 ms window at 16 ms spacing → 7 frames (t=0,16,32,48,64,80,96).
        await Assert.That(rendered).IsEqualTo(7);
        await Assert.That(ticker.SuppressedFrames).IsEqualTo(93);
    }
}
