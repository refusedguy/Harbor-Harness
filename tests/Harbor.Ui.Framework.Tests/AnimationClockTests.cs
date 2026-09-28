using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Tests;

/// <summary>ENG5 (issue #276): background ~10 Hz animation ticker.</summary>
public class AnimationClockTests
{
    [Test]
    public async Task Cadence_Is_TenHertz()
    {
        await Assert.That(AnimationClock.TargetHertz).IsEqualTo(10);
        await Assert.That(AnimationClock.Interval).IsEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task Advance_Increments_And_Raises_Event()
    {
        using var clock = new AnimationClock();
        var seen = new List<long>();
        clock.Advanced += (_, e) => seen.Add(e.Tick);

        await Assert.That(clock.Tick).IsEqualTo(0);
        long t1 = clock.Advance();
        long t2 = clock.Advance();

        await Assert.That(t1).IsEqualTo(1);
        await Assert.That(t2).IsEqualTo(2);
        await Assert.That(clock.Tick).IsEqualTo(2);
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0]).IsEqualTo(1);
        await Assert.That(seen[1]).IsEqualTo(2);
    }

    [Test]
    public async Task Start_Stop_Are_Idempotent()
    {
        using var clock = new AnimationClock();
        await Assert.That(clock.IsRunning).IsFalse();

        clock.Start();
        clock.Start();
        await Assert.That(clock.IsRunning).IsTrue();

        clock.Stop();
        clock.Stop();
        await Assert.That(clock.IsRunning).IsFalse();
    }

    [Test]
    public async Task Background_Timer_Ticks_Without_Manual_Steps()
    {
        using var clock = new AnimationClock(start: true);
        try
        {
            // 10 Hz → first tick lands in ~100 ms; 10 s budget is generous for loaded CI.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (clock.Tick == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            await Assert.That(clock.Tick).IsGreaterThan(0);
        }
        finally
        {
            clock.Stop();
        }
    }

    [Test]
    public async Task Dispose_Stops_Timer()
    {
        var clock = new AnimationClock(start: true);
        await Assert.That(clock.IsRunning).IsTrue();
        clock.Dispose();
        await Assert.That(clock.IsRunning).IsFalse();
    }

    [Test]
    public async Task Repeated_Start_Does_Not_Starve_Ticks()
    {
        // #344: the REPL heartbeat re-starts the clock on every frame (≥12 Hz
        // while a run is active). Re-arming the 100 ms timer on every Start
        // reset its phase faster than it could elapse, so OnTimer never fired
        // and the spinner/mascot painted frozen cells. Start is
        // phase-preserving now: ticks land on the original cadence.
        using var clock = new AnimationClock();
        clock.Start();
        try
        {
            // Re-start every 20 ms (faster than the 100 ms period — the #344
            // pattern). Buggy code yields zero ticks in the whole budget;
            // fixed code ticks at ~100 ms intervals. 10 s budget matches the
            // generous loaded-CI pattern used above.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (clock.Tick < 2 && DateTime.UtcNow < deadline)
            {
                clock.Start();
                await Task.Delay(20);
            }

            await Assert.That(clock.Tick).IsGreaterThan(1);
        }
        finally
        {
            clock.Stop();
        }
    }
}
