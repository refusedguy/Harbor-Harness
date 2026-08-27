using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;

namespace Harbor.DesignSystem.Tests;

/// <summary>Static palette state — serialized to avoid cross-test theme races.</summary>
[NotInParallel]
public class PaletteOverrideTests
{
    [After(Test)]
    public void ResetPalette()
    {
        TerminalColorPalette.SetOverrides(null);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
    }

    [Test]
    public async Task EffectiveTheme_NoOverrides_ReturnsCurrent()
    {
        var theme = TerminalColorPalette.EffectiveTheme("sidebar");
        await Assert.That(ReferenceEquals(theme, TerminalColorPalette.Current)).IsTrue();
    }

    [Test]
    public async Task EffectiveTheme_ScopedPatch_MergesOverCurrent()
    {
        var patch = new PartialTheme(Accent: new RgbColor(0x12, 0x34, 0x56));
        TerminalColorPalette.SetOverrides(new ThemeOverrideSet().With("sidebar", patch));

        var sidebar = TerminalColorPalette.EffectiveTheme("sidebar");
        var composer = TerminalColorPalette.EffectiveTheme("composer");

        await Assert.That(sidebar.Accent).IsEqualTo(new RgbColor(0x12, 0x34, 0x56));
        await Assert.That(composer.Accent).IsEqualTo(HarborTheme.HarborDark.Accent);
    }

    [Test]
    public async Task SetOverrides_Null_Clears()
    {
        TerminalColorPalette.SetOverrides(new ThemeOverrideSet().With("sidebar", new PartialTheme(Accent: new RgbColor(1, 2, 3))));
        TerminalColorPalette.SetOverrides(null);

        await Assert.That(TerminalColorPalette.EffectiveTheme("sidebar").Accent)
            .IsEqualTo(HarborTheme.HarborDark.Accent);
    }

    [Test]
    public async Task SetOverrides_FiresThemeChanged()
    {
        int fired = 0;
        EventHandler handler = (_, _) => fired++;
        TerminalColorPalette.ThemeChanged += handler;
        try
        {
            TerminalColorPalette.SetOverrides(new ThemeOverrideSet());
            await Assert.That(fired).IsEqualTo(1);
        }
        finally
        {
            TerminalColorPalette.ThemeChanged -= handler;
        }
    }
}
