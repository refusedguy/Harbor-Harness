using Harbor.Tui.CellForge.Capabilities;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>Inline-image capability detection matrix (osc-sprint §1337):
/// env-driven, override-wins, multiplexer-guarded — mirrors the
/// CapabilityProber detection discipline.</summary>
public class InlineImageProbeTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Test]
    public async Task KittyEnvs_RouteToApc_PngNativeProtocol()
    {
        await Assert.That(InlineImageProbe.Detect(Env(("KITTY_WINDOW_ID", "1")))).IsEqualTo(InlineImageKind.KittyApc);
        await Assert.That(InlineImageProbe.Detect(Env(("KITTY_PID", "42")))).IsEqualTo(InlineImageKind.KittyApc);
        await Assert.That(InlineImageProbe.Detect(Env(("TERM", "xterm-kitty")))).IsEqualTo(InlineImageKind.KittyApc);
    }

    [Test]
    public async Task Iterm2Family_RouteToOsc1337()
    {
        await Assert.That(InlineImageProbe.Detect(Env(("ITERM_SESSION_ID", "w0t0p0")))).IsEqualTo(InlineImageKind.Osc1337);
        await Assert.That(InlineImageProbe.Detect(Env(("TERM_PROGRAM", "iTerm.app")))).IsEqualTo(InlineImageKind.Osc1337);
        await Assert.That(InlineImageProbe.Detect(Env(("TERM_PROGRAM", "WezTerm")))).IsEqualTo(InlineImageKind.Osc1337);
        await Assert.That(InlineImageProbe.Detect(Env(("WEZTERM_EXECUTABLE", "/usr/bin/wezterm")))).IsEqualTo(InlineImageKind.Osc1337);
        await Assert.That(InlineImageProbe.Detect(Env(("KONSOLE_VERSION", "220803")))).IsEqualTo(InlineImageKind.Osc1337);
        await Assert.That(InlineImageProbe.Detect(Env(("TERM_PROGRAM", "mintty")))).IsEqualTo(InlineImageKind.Osc1337);
    }

    [Test]
    public async Task UnknownTerminal_FallsBackToNone()
    {
        await Assert.That(InlineImageProbe.Detect(Env(("TERM", "xterm-256color")))).IsEqualTo(InlineImageKind.None);
        await Assert.That(InlineImageProbe.Detect(Env())).IsEqualTo(InlineImageKind.None);
    }

    [Test]
    public async Task Multiplexer_GuardsBothFamilies_PassthroughOutOfScope()
    {
        await Assert.That(InlineImageProbe.Detect(Env(
            ("TMUX", "/run/tmux-1000/default,123,0"),
            ("KITTY_WINDOW_ID", "1")))).IsEqualTo(InlineImageKind.None);

        await Assert.That(InlineImageProbe.Detect(Env(
            ("STY", "1234.pts-0.host"),
            ("TERM_PROGRAM", "iTerm.app")))).IsEqualTo(InlineImageKind.None);
    }

    [Test]
    public async Task ExplicitOverride_WinsOverDetection()
    {
        await Assert.That(InlineImageProbe.Detect(Env(
            ("HARBOR_INLINE_IMAGE", "off"), ("KITTY_WINDOW_ID", "1")))).IsEqualTo(InlineImageKind.None);

        await Assert.That(InlineImageProbe.Detect(Env(
            ("HARBOR_INLINE_IMAGE", "osc1337"), ("TERM", "xterm-256color")))).IsEqualTo(InlineImageKind.Osc1337);

        await Assert.That(InlineImageProbe.Detect(Env(
            ("HARBOR_INLINE_IMAGE", "kitty")))).IsEqualTo(InlineImageKind.KittyApc);

        // Case-insensitive + whitespace-tolerant.
        await Assert.That(InlineImageProbe.Detect(Env(("HARBOR_INLINE_IMAGE", "  Kitty ")))).IsEqualTo(InlineImageKind.KittyApc);
    }
}
