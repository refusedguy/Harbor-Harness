using Harbor.Tui.CellForge.Rendering;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Pure-shape tests for the #1173 tab-marker settle: opencode's sweep is
///     not ported (no Renderable clock on cell-diff), so only the smootherstep
///     envelope and the frame-counted drain level live here — both clock-free.
/// </summary>
public class TabMarkerFadeTests
{
    [Test]
    public async Task Smootherstep_PinsEnds_AndPassesThroughHalf()
    {
        await Assert.That(GlowEffect.Smootherstep(0.0)).IsEqualTo(0.0);
        await Assert.That(GlowEffect.Smootherstep(1.0)).IsEqualTo(1.0);
        await Assert.That(GlowEffect.Smootherstep(0.5)).IsEqualTo(0.5);
    }

    [Test]
    public async Task Smootherstep_ClampsOutsideUnitRange()
    {
        await Assert.That(GlowEffect.Smootherstep(-2.0)).IsEqualTo(0.0);
        await Assert.That(GlowEffect.Smootherstep(2.0)).IsEqualTo(1.0);
    }

    [Test]
    public async Task TabMarkerFade_RunsOneToZero_OverTheFade()
    {
        const int ticks = 4;
        await Assert.That(GlowEffect.TabMarkerFade(ticks, ticks)).IsEqualTo(1.0);
        await Assert.That(GlowEffect.TabMarkerFade(0, ticks)).IsEqualTo(0.0);

        double half = GlowEffect.TabMarkerFade(2, ticks);
        await Assert.That(half).IsEqualTo(0.5);

        // Draining: fewer ticks left means a dimmer marker, monotonically.
        double three = GlowEffect.TabMarkerFade(3, ticks);
        double one = GlowEffect.TabMarkerFade(1, ticks);
        await Assert.That(three).IsGreaterThan(half);
        await Assert.That(half).IsGreaterThan(one);
        await Assert.That(one).IsGreaterThan(0.0);
    }

    [Test]
    public async Task TabMarkerFade_DegenerateInput_SettlesImmediately()
    {
        await Assert.That(GlowEffect.TabMarkerFade(-1, 4)).IsEqualTo(0.0);
        await Assert.That(GlowEffect.TabMarkerFade(2, 0)).IsEqualTo(0.0);
        await Assert.That(GlowEffect.TabMarkerFade(2, -3)).IsEqualTo(0.0);
    }
}
