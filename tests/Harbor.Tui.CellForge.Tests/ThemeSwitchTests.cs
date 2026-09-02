using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Theme switching contract: TerminalColorPalette.Apply swaps tokens atomically
/// and ChatPalette re-projects its styles; re-applying the same instance is a
/// no-op. Tests restore HarborDark and run serialized — the palette is global
/// static state shared with every other painter in this assembly.
/// </summary>
[NotInParallel]
public class ThemeSwitchTests
{
    [After(Test)]
    public void RestoreDefaultTheme() => TerminalColorPalette.Apply(HarborTheme.HarborDark);

    [Test]
    public async Task Apply_ChangesTokenReads()
    {
        RgbColor before = TerminalColorPalette.Accent;
        TerminalColorPalette.Apply(HarborTheme.HarborLight);

        await Assert.That(TerminalColorPalette.Accent).IsNotEqualTo(before);
        await Assert.That(TerminalColorPalette.Accent).IsEqualTo(HarborTheme.HarborLight.Accent);
    }

    [Test]
    public async Task Apply_SameInstance_IsNoOpWithoutEvent()
    {
        int events = 0;
        TerminalColorPalette.ThemeChanged += OnChanged;
        try
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark); // already current
            TerminalColorPalette.Apply(HarborTheme.HarborCool); // real change
        }
        finally
        {
            TerminalColorPalette.ThemeChanged -= OnChanged;
        }

        await Assert.That(events).IsEqualTo(1);
        await Assert.That(TerminalColorPalette.Current).IsEqualTo(HarborTheme.HarborCool);

        void OnChanged(object? _, EventArgs __) => events++;
    }

    [Test]
    public async Task ThemeChanged_FiresOnSwitch()
    {
        int events = 0;
        TerminalColorPalette.ThemeChanged += OnChanged;
        try
        {
            TerminalColorPalette.Apply(HarborTheme.HarborWarm);
        }
        finally
        {
            TerminalColorPalette.ThemeChanged -= OnChanged;
        }

        await Assert.That(events).IsEqualTo(1);

        void OnChanged(object? _, EventArgs __) => events++;
    }

    [Test]
    public async Task ChatPalette_ReprojectsOnThemeSwitch()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborLight);
        await Assert.That(ChatPalette.Accent).IsEqualTo(PackedColor.Rgb(
            HarborTheme.HarborLight.Accent.R,
            HarborTheme.HarborLight.Accent.G,
            HarborTheme.HarborLight.Accent.B));

        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        await Assert.That(ChatPalette.Accent).IsEqualTo(PackedColor.Rgb(0x39, 0xBA, 0xE6));

        // Derived styles follow the catalog too.
        await Assert.That(ChatPalette.ToolOk.Fg).IsEqualTo(PackedColor.Rgb(0x7F, 0xD9, 0x62));
    }

    [Test]
    public async Task BuiltInCatalog_ContainsFourThemes()
    {
        await Assert.That(HarborTheme.BuiltIn).Count().IsEqualTo(4);
        await Assert.That(HarborTheme.ByName("harbor-warm")).IsEqualTo(HarborTheme.HarborWarm);
        await Assert.That(HarborTheme.ByName("HARBOR-LIGHT")).IsEqualTo(HarborTheme.HarborLight);
        await Assert.That(HarborTheme.ByName("nope")).IsEqualTo(HarborTheme.HarborDark);
    }
}
