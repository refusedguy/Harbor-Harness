using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;

namespace Harbor.DesignSystem.Tests;

public class TerminalBackgroundProbeTests
{
    [Test]
    public async Task TryParseOsc11_BelTerminated_8Bit_Parses()
    {
        bool ok = TerminalBackgroundProbe.TryParseOsc11("\u001B]11;rgb:0a/0e/14\u0007", out RgbColor bg);
        await Assert.That(ok).IsTrue();
        await Assert.That(bg.R).IsEqualTo((byte)0x0A);
        await Assert.That(bg.G).IsEqualTo((byte)0x0E);
        await Assert.That(bg.B).IsEqualTo((byte)0x14);
    }

    [Test]
    public async Task TryParseOsc11_StTerminated_16Bit_Parses()
    {
        bool ok = TerminalBackgroundProbe.TryParseOsc11("\u001B]11;rgb:ffff/ffff/ffff\u001B\\", out RgbColor bg);
        await Assert.That(ok).IsTrue();
        await Assert.That(bg.R).IsEqualTo((byte)0xFF);
        await Assert.That(bg.G).IsEqualTo((byte)0xFF);
        await Assert.That(bg.B).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task TryParseOsc11_MixedForms_Parses()
    {
        bool ok = TerminalBackgroundProbe.TryParseOsc11("\u001B]11;rgb:ab/cdef/12\u0007", out RgbColor bg);
        await Assert.That(ok).IsTrue();
        await Assert.That(bg.R).IsEqualTo((byte)0xAB);
        await Assert.That(bg.G).IsEqualTo((byte)0xCD);
        await Assert.That(bg.B).IsEqualTo((byte)0x12);
    }

    [Test]
    public async Task TryParseOsc11_Garbage_ReturnsFalse()
    {
        await Assert.That(TerminalBackgroundProbe.TryParseOsc11(null, out _)).IsFalse();
        await Assert.That(TerminalBackgroundProbe.TryParseOsc11("", out _)).IsFalse();
        await Assert.That(TerminalBackgroundProbe.TryParseOsc11("\u001B]11;butt\u0007", out _)).IsFalse();
        await Assert.That(TerminalBackgroundProbe.TryParseOsc11("\u001B]10;rgb:0a/0e/14\u0007", out _)).IsFalse();
        await Assert.That(TerminalBackgroundProbe.TryParseOsc11("\u001B]11;rgb:zz/0e/14\u0007", out _)).IsFalse();
    }

    [Test]
    public async Task RelativeLuminance_BlackAndWhite()
    {
        await Assert.That(TerminalBackgroundProbe.RelativeLuminance(new RgbColor(0, 0, 0))).IsEqualTo(0.0).Within(0.001);
        await Assert.That(TerminalBackgroundProbe.RelativeLuminance(new RgbColor(255, 255, 255))).IsEqualTo(1.0).Within(0.001);
    }

    [Test]
    public async Task PickTheme_DarkReport_HarborDark()
    {
        var theme = TerminalBackgroundProbe.PickTheme(new RgbColor(0x0A, 0x0E, 0x14));
        await Assert.That(theme.Name).IsEqualTo("harbor-dark");
    }

    [Test]
    public async Task PickTheme_LightReport_HarborLight()
    {
        var theme = TerminalBackgroundProbe.PickTheme(new RgbColor(0xF5, 0xF3, 0xEF));
        await Assert.That(theme.Name).IsEqualTo("harbor-light");
    }

    [Test]
    public async Task Detect_Unparsable_FallsBackToHarborDark()
    {
        await Assert.That(TerminalBackgroundProbe.Detect(null).Name).IsEqualTo("harbor-dark");
        await Assert.That(TerminalBackgroundProbe.Detect("garbage").Name).IsEqualTo("harbor-dark");
    }

    [Test]
    public async Task Detect_LightResponse_PicksHarborLight()
    {
        await Assert.That(TerminalBackgroundProbe.Detect("\u001B]11;rgb:ff/ff/ff\u0007").Name).IsEqualTo("harbor-light");
    }
}
