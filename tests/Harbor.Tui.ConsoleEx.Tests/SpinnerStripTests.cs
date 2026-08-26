using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class SpinnerStripTests
{
    [Test]
    public async Task WorkingFrames_CycleEveryTick()
    {
        var seen = new List<string>();
        for (long tick = 0; tick < SpinnerStrip.WorkingFrames.Length * 2; tick++)
        {
            string frame = new(SpinnerStrip.Frame(tick));
            if (tick < SpinnerStrip.WorkingFrames.Length)
            {
                await Assert.That(frame).IsEqualTo(SpinnerStrip.WorkingFrames[(int)tick]);
            }

            seen.Add(frame);
        }

        // Full cycle repeats.
        for (int i = 0; i < SpinnerStrip.WorkingFrames.Length; i++)
        {
            await Assert.That(seen[i]).IsEqualTo(seen[SpinnerStrip.WorkingFrames.Length + i]);
        }
    }

    [Test]
    public async Task AwaitingRhythm_PulsesOncePerPeriod()
    {
        // Same frame across the whole pulse period…
        string at0 = new(SpinnerStrip.Frame(0, SpinnerRhythm.Awaiting));
        string at3 = new(SpinnerStrip.Frame(PulseSafeLast(3), SpinnerRhythm.Awaiting));
        await Assert.That(at0).IsEqualTo(at3);

        // …then advances exactly once.
        string next = new(SpinnerStrip.Frame(PulseSafeNext(), SpinnerRhythm.Awaiting));
        await Assert.That(next).IsNotEqualTo(at0);
        await Assert.That(next).IsEqualTo(SpinnerStrip.AwaitingFrames[1]);

        static long PulseSafeLast(long t) => t;
        static long PulseSafeNext() => SpinnerStrip.PulsePeriod;
    }

    [Test]
    public async Task Rhythms_DifferAtSameTick_SoUserSeesStateChange()
    {
        string working = new(SpinnerStrip.Frame(5, SpinnerRhythm.Working));
        string awaiting = new(SpinnerStrip.Frame(5, SpinnerRhythm.Awaiting));
        await Assert.That(working).IsNotEqualTo(awaiting);
    }

    [Test]
    public async Task Glyphs_AreSingleCellWidth()
    {
        foreach (var f in SpinnerStrip.WorkingFrames.Concat(SpinnerStrip.AwaitingFrames))
        {
            await Assert.That(Harbor.Tui.ConsoleEx.Rendering.UnicodeWidth.Width(f)).IsEqualTo(1);
        }
    }
}
