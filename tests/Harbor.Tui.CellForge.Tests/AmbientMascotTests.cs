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
        foreach (var mood in Enum.GetValues<MascotMood>())
        {
            var frames = AmbientMascot.FramesOf(mood);

            int width = AmbientMascot.Width(frames[0]);
            await Assert.That(width).IsGreaterThan(0);
            foreach (var frame in frames)
            {
                await Assert.That(AmbientMascot.Width(frame)).IsEqualTo(width);
            }
        }
    }

    [Test]
    public async Task FrameIndex_MatchesFrameSelection()
    {
        foreach (var mood in Enum.GetValues<MascotMood>())
        {
            var frames = AmbientMascot.FramesOf(mood);
            for (long tick = -10; tick < 40; tick++)
            {
                int idx = AmbientMascot.FrameIndex(tick, mood);
                await Assert.That(AmbientMascot.Frame(tick, mood)).IsEqualTo(frames[idx]);
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
        foreach (var mood in Enum.GetValues<MascotMood>())
        {
            await Assert.That(AmbientMascot.Frame(-5, mood)).IsNotNull();
        }
    }

    [Test]
    public async Task Frames_AreAsciiSingleWidth()
    {
        foreach (var mood in Enum.GetValues<MascotMood>())
        {
            foreach (string frame in AmbientMascot.FramesOf(mood))
            {
                foreach (char c in frame)
                {
                    await Assert.That(c < 128).IsTrue();
                }
            }
        }
    }
}
