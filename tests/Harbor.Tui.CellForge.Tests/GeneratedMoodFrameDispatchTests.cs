namespace Harbor.Tui.CellForge.Tests;

using Harbor.Tui.CellForge.Widgets;

public class GeneratedMoodFrameDispatchTests
{
    [Test]
    public async Task FramesOf_Idle_ReturnsIdleFrames()
    {
        var frames = MascotMoodFrameDispatch.FramesOf(MascotMood.Idle);
        await Assert.That(frames).IsEqualTo(AmbientMascot.IdleFrames);
    }

    [Test]
    public async Task FramesOf_Working_ReturnsWorkingFrames()
    {
        var frames = MascotMoodFrameDispatch.FramesOf(MascotMood.Working);
        await Assert.That(frames).IsEqualTo(AmbientMascot.WorkingFrames);
    }

    [Test]
    public async Task FramesOf_Error_ReturnsErrorFrames()
    {
        var frames = MascotMoodFrameDispatch.FramesOf(MascotMood.Error);
        await Assert.That(frames).IsEqualTo(AmbientMascot.ErrorFrames);
    }

    [Test]
    public async Task FramesOf_Success_ReturnsSuccessFrames()
    {
        var frames = MascotMoodFrameDispatch.FramesOf(MascotMood.Success);
        await Assert.That(frames).IsEqualTo(AmbientMascot.SuccessFrames);
    }

    [Test]
    public async Task PanelEarsOf_Error_ReturnsFlatEars()
    {
        var ears = MascotMoodFrameDispatch.PanelEarsOf(MascotMood.Error);
        await Assert.That(ears).IsEqualTo(AmbientMascot.EarsFlat);
    }

    [Test]
    public async Task PanelEarsOf_Idle_ReturnsUpEars()
    {
        var ears = MascotMoodFrameDispatch.PanelEarsOf(MascotMood.Idle);
        await Assert.That(ears).IsEqualTo(AmbientMascot.EarsUp);
    }

    [Test]
    public async Task PanelPawsOf_Working_ReturnsKneadPaws()
    {
        var paws = MascotMoodFrameDispatch.PanelPawsOf(MascotMood.Working);
        await Assert.That(paws).IsEqualTo(AmbientMascot.PawsKnead);
    }

    [Test]
    public async Task PanelPawsOf_Awaiting_ReturnsReachPaws()
    {
        var paws = MascotMoodFrameDispatch.PanelPawsOf(MascotMood.Awaiting);
        await Assert.That(paws).IsEqualTo(AmbientMascot.PawsReach);
    }

    [Test]
    public async Task PanelPawsOf_Success_ReturnsWagPaws()
    {
        var paws = MascotMoodFrameDispatch.PanelPawsOf(MascotMood.Success);
        await Assert.That(paws).IsEqualTo(AmbientMascot.PawsWag);
    }
}
