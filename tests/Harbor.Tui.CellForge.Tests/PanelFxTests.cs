using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>PanelFx — pure motion-primitive contracts (HDS v1 timings).</summary>
public class PanelFxTests
{
    [Test]
    public async Task Durations_Map_To_Frames_AtDisplayCadence()
    {
        // 150 ms micro → 9 frames, 300 ms standard → 18 frames at 60 fps.
        await Assert.That(PanelFx.FadeFrames).IsEqualTo(9);
        await Assert.That(PanelFx.SlideFrames).IsEqualTo(18);
        await Assert.That(PanelFx.PulseFrames).IsGreaterThan(0);
    }

    [Test]
    public async Task Progress_BeforeStart_IsSettled()
    {
        await Assert.That(PanelFx.Progress(startTick: 10, nowTick: 10, PanelFx.FadeFrames)).IsEqualTo(1.0);
        await Assert.That(PanelFx.Progress(startTick: 20, nowTick: 5, PanelFx.FadeFrames)).IsEqualTo(1.0);
    }

    [Test]
    public async Task Progress_EasesOut_AndClampsAtOne()
    {
        double first = PanelFx.Progress(0, 1, PanelFx.FadeFrames);
        double half = PanelFx.Progress(0, PanelFx.FadeFrames / 2, PanelFx.FadeFrames);
        double full = PanelFx.Progress(0, PanelFx.FadeFrames * 3, PanelFx.FadeFrames);

        await Assert.That(first > 0).IsTrue();            // ease-out overshoots linear
        await Assert.That(first > (1.0 / PanelFx.FadeFrames)).IsTrue();
        await Assert.That(half > 0.5 && half < 1.0).IsTrue();
        await Assert.That(full).IsEqualTo(1.0);
    }

    [Test]
    public async Task WarnPulse_SymmetricAroundZeroBirthGuarded()
    {
        await Assert.That(PanelFx.WarnPulse(birthTick: -1, nowTick: 999)).IsEqualTo(0.0);
        await Assert.That(PanelFx.WarnPulse(10, nowTick: 10)).IsEqualTo(0.0);

        double mid = PanelFx.WarnPulse(0, nowTick: PanelFx.PulseFrames / 4);   // peak
        double trough = PanelFx.WarnPulse(0, nowTick: PanelFx.PulseFrames * 3 / 4); // negative sine → clamp

        await Assert.That(mid > 0.99).IsTrue();
        await Assert.That(trough).IsEqualTo(0.0);
    }

    [Test]
    public async Task Lerp_InterpolatesChannels()
    {
        var from = ChatPalette.Panel;
        var to = ChatPalette.Accent;

        var mid = PanelFx.Lerp(from, to, 0.5);
        var end = PanelFx.Lerp(from, to, 1.0);

        int midR = (from.RgbChannels.R + to.RgbChannels.R) / 2;
        await Assert.That(mid.RgbChannels.R).IsBetween((byte)(midR - 1), (byte)(midR + 1));
        await Assert.That(end).IsEqualTo(to);
    }

    [Test]
    public async Task WithAlpha_FullAlpha_ReturnsSameStyle()
    {
        var boldAccent = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);

        await Assert.That(PanelFx.WithAlpha(boldAccent, 1.0) == boldAccent).IsTrue();
        await Assert.That(PanelFx.WithAlpha(CellStyle.Plain, 0.25) == CellStyle.Plain).IsTrue();
    }

    [Test]
    public async Task WithAlpha_ZeroAlpha_FadesTowardPanelSurface()
    {
        var faded = PanelFx.WithAlpha(new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold), 0.0);

        await Assert.That(faded.Fg).IsEqualTo(ChatPalette.Panel);
        await Assert.That(faded.Attrs == StyleAttr.Bold).IsTrue(); // attributes ride through the fade
    }
}
