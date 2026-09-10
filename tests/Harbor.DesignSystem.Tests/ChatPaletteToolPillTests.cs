using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.DesignSystem.Tests;

/// <summary>Tool status pill styles — serialized (shared key) to avoid cross-test theme races.</summary>
[NotInParallel("terminal-color-palette")]
public class ChatPaletteToolPillTests
{
    [After(Test)]
    public void ResetPalette()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
    }

    [Test]
    public async Task ToolPillRunning_MatchesWarningTokenAndGlyphStyle()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);

        var warning = TerminalColorPalette.Current.Warning;
        var expected = PackedColor.Rgb(warning.R, warning.G, warning.B);

        await Assert.That(ChatPalette.ToolPillRunning.Fg).IsEqualTo(expected);
        await Assert.That(ChatPalette.ToolPillRunning.Fg).IsEqualTo(ChatPalette.Warning);
        await Assert.That(ChatPalette.ToolPillRunning).IsEqualTo(ChatPalette.ToolRunning);
    }

    [Test]
    public async Task ToolPillOk_MatchesSuccessTokenAndGlyphStyle()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);

        var success = TerminalColorPalette.Current.Success;
        var expected = PackedColor.Rgb(success.R, success.G, success.B);

        await Assert.That(ChatPalette.ToolPillOk.Fg).IsEqualTo(expected);
        await Assert.That(ChatPalette.ToolPillOk.Fg).IsEqualTo(ChatPalette.Success);
        await Assert.That(ChatPalette.ToolPillOk).IsEqualTo(ChatPalette.ToolOk);
    }

    [Test]
    public async Task ToolPillError_MatchesErrorTokenAndGlyphStyle()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);

        var error = TerminalColorPalette.Current.Error;
        var expected = PackedColor.Rgb(error.R, error.G, error.B);

        await Assert.That(ChatPalette.ToolPillError.Fg).IsEqualTo(expected);
        await Assert.That(ChatPalette.ToolPillError.Fg).IsEqualTo(ChatPalette.Error);
        await Assert.That(ChatPalette.ToolPillError).IsEqualTo(ChatPalette.ToolError);
    }

    [Test]
    public async Task ToolPills_AreMutuallyDistinguishable()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);

        await Assert.That(ChatPalette.ToolPillRunning.Fg).IsNotEqualTo(ChatPalette.ToolPillOk.Fg);
        await Assert.That(ChatPalette.ToolPillRunning.Fg).IsNotEqualTo(ChatPalette.ToolPillError.Fg);
        await Assert.That(ChatPalette.ToolPillOk.Fg).IsNotEqualTo(ChatPalette.ToolPillError.Fg);
        await Assert.That(ChatPalette.ToolPillRunning).IsNotEqualTo(ChatPalette.ToolPillOk);
        await Assert.That(ChatPalette.ToolPillRunning).IsNotEqualTo(ChatPalette.ToolPillError);
        await Assert.That(ChatPalette.ToolPillOk).IsNotEqualTo(ChatPalette.ToolPillError);
    }

    [Test]
    public async Task ToolPills_RebuildOnHarborLight()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborLight);

        var warning = HarborTheme.HarborLight.Warning;
        var success = HarborTheme.HarborLight.Success;
        var error = HarborTheme.HarborLight.Error;

        await Assert.That(ChatPalette.ToolPillRunning.Fg)
            .IsEqualTo(PackedColor.Rgb(warning.R, warning.G, warning.B));
        await Assert.That(ChatPalette.ToolPillOk.Fg)
            .IsEqualTo(PackedColor.Rgb(success.R, success.G, success.B));
        await Assert.That(ChatPalette.ToolPillError.Fg)
            .IsEqualTo(PackedColor.Rgb(error.R, error.G, error.B));

        var darkWarning = HarborTheme.HarborDark.Warning;
        await Assert.That(ChatPalette.ToolPillRunning.Fg)
            .IsNotEqualTo(PackedColor.Rgb(darkWarning.R, darkWarning.G, darkWarning.B));
    }
}
