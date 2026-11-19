using Harbor.Tui.CellForge.Capabilities;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>F132 env-only system-theme detection matrix: override-wins,
/// NO_COLOR disable, truecolor/256 fallback and COLORFGBG background hints.</summary>
public class SystemThemeProbeTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Test]
    public async Task Truecolor_Beats256_AndIsCaseInsensitive()
    {
        await Assert.That(SystemThemeProbe.DetectDepth(Env(("COLORTERM", "truecolor"))))
            .IsEqualTo(TerminalColorDepth.TrueColor);
        await Assert.That(SystemThemeProbe.DetectDepth(Env(("COLORTERM", " 24BIT "), ("TERM", "xterm-256color"))))
            .IsEqualTo(TerminalColorDepth.TrueColor);
    }

    [Test]
    public async Task Term256_FallsBackToStandard()
    {
        await Assert.That(SystemThemeProbe.DetectDepth(Env(("TERM", "xterm-256color"))))
            .IsEqualTo(TerminalColorDepth.Colors256);
        await Assert.That(SystemThemeProbe.DetectDepth(Env(("TERM", "screen-256color"))))
            .IsEqualTo(TerminalColorDepth.Colors256);
        await Assert.That(SystemThemeProbe.DetectDepth(Env(("TERM", "xterm"))))
            .IsEqualTo(TerminalColorDepth.Standard);
        await Assert.That(SystemThemeProbe.DetectDepth(Env()))
            .IsEqualTo(TerminalColorDepth.Standard);
    }

    [Test]
    public async Task NoColor_Disables_AndFlattensEffectiveDepth()
    {
        var env = Env(("NO_COLOR", "1"), ("COLORTERM", "truecolor"));
        await Assert.That(SystemThemeProbe.IsColourDisabled(env)).IsTrue();
        SystemThemeProfile profile = SystemThemeProbe.Detect(env);
        await Assert.That(profile.ColorsDisabled).IsTrue();
        await Assert.That(profile.Depth).IsEqualTo(TerminalColorDepth.TrueColor);
        await Assert.That(profile.EffectiveDepth).IsEqualTo(TerminalColorDepth.Standard);
    }

    [Test]
    public async Task EmptyNoColor_DoesNotDisable()
    {
        await Assert.That(SystemThemeProbe.IsColourDisabled(Env(("NO_COLOR", "")))).IsFalse();
        await Assert.That(SystemThemeProbe.IsColourDisabled(Env())).IsFalse();
    }

    [Test]
    public async Task ExplicitOverride_WinsOverDetection()
    {
        var off = Env(("HARBOR_THEME_SYSTEM", " OFF "), ("COLORTERM", "truecolor"), ("NO_COLOR", ""));
        await Assert.That(SystemThemeProbe.IsColourDisabled(off)).IsTrue();

        var on = Env(("HARBOR_THEME_SYSTEM", "on"), ("COLORTERM", "truecolor"));
        await Assert.That(SystemThemeProbe.IsColourDisabled(on)).IsFalse();
        await Assert.That(SystemThemeProbe.Detect(on).Depth).IsEqualTo(TerminalColorDepth.TrueColor);
    }

    [Test]
    public async Task ColorFgbg_BackgroundFamily_AndMalformedFallback()
    {
        await Assert.That(SystemThemeProbe.DetectBackground(Env(("COLORFGBG", "15;0"))))
            .IsEqualTo(TerminalBackground.Dark);
        await Assert.That(SystemThemeProbe.DetectBackground(Env(("COLORFGBG", "0;7"))))
            .IsEqualTo(TerminalBackground.Light);
        await Assert.That(SystemThemeProbe.DetectBackground(Env(("COLORFGBG", "0;15"))))
            .IsEqualTo(TerminalBackground.Light);
        await Assert.That(SystemThemeProbe.DetectBackground(Env(("COLORFGBG", "7;0"))))
            .IsEqualTo(TerminalBackground.Dark);
        await Assert.That(SystemThemeProbe.DetectBackground(Env(("COLORFGBG", "garbage"))))
            .IsEqualTo(TerminalBackground.Unknown);
        await Assert.That(SystemThemeProbe.DetectBackground(Env()))
            .IsEqualTo(TerminalBackground.Unknown);
    }

    [Test]
    public async Task Detect_ComposesAllThreeSignals()
    {
        SystemThemeProfile profile = SystemThemeProbe.Detect(Env(
            ("COLORTERM", "truecolor"),
            ("COLORFGBG", "15;0")));
        await Assert.That(profile.Depth).IsEqualTo(TerminalColorDepth.TrueColor);
        await Assert.That(profile.Background).IsEqualTo(TerminalBackground.Dark);
        await Assert.That(profile.ColorsDisabled).IsFalse();
    }
}
