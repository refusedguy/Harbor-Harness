namespace Harbor.Tui.RendererTests;

using System.Text;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Tui.RendererTests.Support;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Golden coverage for the <see cref="MoodFrameGenerator" /> dispatch
///     (codegen-boilerplate sprint Task 3): the [MoodFrame]-generated
///     <see cref="MascotMoodFrames" /> table must reproduce the frame banks
///     and the tick→index math (Sleeping period 8, negative-tick wrap) that
///     the removed hand-written switch in <see cref="AmbientMascot" /> used
///     to own. The golden frame pins a 25-tick × 8-mood rendering matrix.
/// </summary>
public class MascotMoodFrameTests
{
    private static readonly MascotMood[] Moods =
    [
        MascotMood.Idle,
        MascotMood.Working,
        MascotMood.Awaiting,
        MascotMood.Sleeping,
        MascotMood.Thinking,
        MascotMood.ToolCall,
        MascotMood.Error,
        MascotMood.Success,
    ];

    [Test]
    public async Task FramesOf_MapsEveryMoodToItsBank()
    {
        // Bank mapping pins the generated switch against the literal art —
        // a generator regression (wrong field resolved) is caught here.
        await Assert.That(MascotMoodFrames.FramesOf(MascotMood.Idle))
            .IsEqualTo(AmbientMascot.IdleFrames);
        await Assert.That(MascotMoodFrames.FramesOf(MascotMood.Working))
            .IsEqualTo(AmbientMascot.WorkingFrames);
        await Assert.That(MascotMoodFrames.FramesOf(MascotMood.Sleeping))
            .IsEqualTo(AmbientMascot.SleepingFrames);
        await Assert.That(MascotMoodFrames.FramesOf(MascotMood.Success))
            .IsEqualTo(AmbientMascot.SuccessFrames);

        // Zero-copy: the dispatch hands out the static banks by reference.
        await Assert.That(ReferenceEquals(MascotMoodFrames.FramesOf(MascotMood.Working), AmbientMascot.WorkingFrames))
            .IsTrue();
    }

    [Test]
    public async Task FrameIndex_SleepingAdvancesEvery8Ticks()
    {
        // Ticks 0..7 stay on frame 0, tick 8 steps to frame 1.
        await Assert.That(MascotMoodFrames.FrameIndex(0, MascotMood.Sleeping)).IsEqualTo(0);
        await Assert.That(MascotMoodFrames.FrameIndex(7, MascotMood.Sleeping)).IsEqualTo(0);
        await Assert.That(MascotMoodFrames.FrameIndex(8, MascotMood.Sleeping)).IsEqualTo(1);
        // Other moods advance every tick.
        await Assert.That(MascotMoodFrames.FrameIndex(0, MascotMood.Working)).IsEqualTo(0);
        await Assert.That(MascotMoodFrames.FrameIndex(1, MascotMood.Working)).IsEqualTo(1);
    }

    [Test]
    public async Task FrameIndex_NegativeTicksWrap()
    {
        // -1 % 4 == -1 → wrapped to 3 (the removed switch's documented wrap).
        await Assert.That(MascotMoodFrames.FrameIndex(-1, MascotMood.Working)).IsEqualTo(3);
        // C# division truncates toward zero: -5 / 8 == 0 → frame 0.
        await Assert.That(MascotMoodFrames.FrameIndex(-5, MascotMood.Sleeping)).IsEqualTo(0);
    }

    [Test]
    public async Task Frame_Matrix_MatchesGolden()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# mascot mood frames — 25 ticks × 8 moods (generated MascotMoodFrames dispatch)");
        sb.Append("# tick ");
        foreach (MascotMood mood in Moods)
        {
            sb.Append($"| {mood,-9}");
        }

        sb.AppendLine();

        for (long tick = 0; tick < 25; tick++)
        {
            sb.Append($"{tick,5} ");
            foreach (MascotMood mood in Moods)
            {
                sb.Append($"| {AmbientMascot.Frame(tick, mood)} ");
            }

            sb.AppendLine();
        }

        await GoldenFrames.AssertGoldenAsync("mascot-mood-frames", sb.ToString());
    }
}
