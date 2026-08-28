using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class AmbientMascotTests
{
    [Test]
    public async Task Frame_CyclesDeterministically()
    {
        string f0 = AmbientMascot.Frame(0, MascotMood.Idle);
        string f1 = AmbientMascot.Frame(1, MascotMood.Idle);
        string fAgain = AmbientMascot.Frame(IdlePeriod, MascotMood.Idle);

        await Assert.That(f0).IsEqualTo(fAgain);
        await Assert.That(f1).IsNotEqualTo(f0);
    }

    private static long IdlePeriod => AmbientMascot.IdleFrames.Length;

    [Test]
    public async Task Frame_AllMoods_ConstantWidth()
    {
        foreach (var mood in new[] { MascotMood.Idle, MascotMood.Working, MascotMood.Awaiting, MascotMood.Sleeping })
        {
            var frames = mood switch
            {
                MascotMood.Working => AmbientMascot.WorkingFrames,
                MascotMood.Awaiting => AmbientMascot.AwaitingFrames,
                MascotMood.Sleeping => AmbientMascot.SleepingFrames,
                _ => AmbientMascot.IdleFrames,
            };

            int width = AmbientMascot.Width(frames[0]);
            foreach (var frame in frames)
            {
                await Assert.That(AmbientMascot.Width(frame)).IsEqualTo(width);
            }
        }
    }

    [Test]
    public async Task Frame_Sleeping_AdvancesSlowly()
    {
        string early = AmbientMascot.Frame(1, MascotMood.Sleeping);
        string later = AmbientMascot.Frame(AmbientMascot.SleepPeriod + 1, MascotMood.Sleeping);
        await Assert.That(early).IsNotEqualTo(later);
    }

    [Test]
    public async Task Frame_NegativeTick_NoThrow()
    {
        await Assert.That(AmbientMascot.Frame(-5, MascotMood.Working)).IsNotNull();
    }

    [Test]
    public async Task Frames_AreAsciiSingleWidth()
    {
        foreach (var frame in AmbientMascot.IdleFrames
                     .Concat(AmbientMascot.WorkingFrames)
                     .Concat(AmbientMascot.AwaitingFrames)
                     .Concat(AmbientMascot.SleepingFrames))
        {
            foreach (char c in frame)
            {
                await Assert.That(c < 128).IsTrue();
            }
        }
    }
}
