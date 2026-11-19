using Harbor.TestKit;
using Harbor.Tui.CellForge.Capabilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #3106: <see cref="SystemThemeProbe.Detect"/> now runs at CellForge
/// renderer startup and its <see cref="SystemThemeProfile"/> feeds palette
/// selection. These tests drive the probe through the renderer's injectable
/// lookup, so no process-wide environment variable is touched.
/// </summary>
public class CellForgeSystemThemeWiringTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    private static CellForgeTuiRenderer Renderer(Func<string, string?> lookup) =>
        new(
            NullLogger<CellForgeTuiRenderer>.Instance,
            new RecordingBackend(),
            statusVm: null,
            chatVm: null,
            inputVm: null,
            store: null,
            sessions: null,
            panelServices: null,
            environmentLookup: lookup);

    [Test]
    public async Task Term256_Selects256Colors()
    {
        using var renderer = Renderer(Env(("TERM", "xterm-256color")));

        await Assert.That(renderer.ThemeProfile.Depth).IsEqualTo(TerminalColorDepth.Colors256);
        await Assert.That(renderer.Palette.ColorCount).IsEqualTo(CellForgePalette.Colors256);
    }

    [Test]
    public async Task NoColor_StaysOn16EvenWithTruecolorAdvertised()
    {
        using var renderer = Renderer(Env(
            ("NO_COLOR", "1"),
            ("COLORTERM", "truecolor")));

        await Assert.That(renderer.ThemeProfile.ColorsDisabled).IsTrue();
        await Assert.That(renderer.ThemeProfile.EffectiveDepth).IsEqualTo(TerminalColorDepth.Standard);
        await Assert.That(renderer.Palette.ColorCount).IsEqualTo(CellForgePalette.Colors16);
    }

    [Test]
    public async Task Truecolor_SelectsTrueColorPalette()
    {
        using var renderer = Renderer(Env(("COLORTERM", "truecolor")));

        await Assert.That(renderer.ThemeProfile.Depth).IsEqualTo(TerminalColorDepth.TrueColor);
        await Assert.That(renderer.Palette.ColorCount).IsEqualTo(CellForgePalette.TrueColorCount);
    }

    [Test]
    public async Task EmptyEnvironment_FallsBackTo16ColorPalette()
    {
        using var renderer = Renderer(Env());

        await Assert.That(renderer.ThemeProfile.Depth).IsEqualTo(TerminalColorDepth.Standard);
        await Assert.That(renderer.ThemeProfile.Background).IsEqualTo(TerminalBackground.Unknown);
        await Assert.That(renderer.Palette.ColorCount).IsEqualTo(CellForgePalette.Colors16);
    }
}
