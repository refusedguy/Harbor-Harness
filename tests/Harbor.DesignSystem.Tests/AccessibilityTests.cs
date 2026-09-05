using Harbor.Ui.Framework.Projection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.DesignSystem.Tests;

/// <summary>
/// WCAG 2.x accessibility contract of the HDS v1 catalog — every surface and
/// role pairing Harbor actually renders. Ratios computed from
/// <see cref="Accessibility.ContrastRatio"/> against the real tokens, so a
/// palette edit that silently breaks readability fails CI.
/// </summary>
[NotInParallel("terminal-color-palette")]
public class AccessibilityTests
{
    private static readonly RgbColor[] DarkSurfaces =
    [
        TerminalColorPalette.Background,
        TerminalColorPalette.Panel,
        TerminalColorPalette.Surface,
        TerminalColorPalette.Surface2,
    ];

    private static readonly (RgbColor Color, string Name)[] Accents =
    [
        (TerminalColorPalette.Accent, "accent"),
        (TerminalColorPalette.Success, "success"),
        (TerminalColorPalette.Warning, "warning"),
        (TerminalColorPalette.Error, "error"),
        (TerminalColorPalette.Tool, "tool"),
        (TerminalColorPalette.System, "system"),
    ];

    [Test]
    public async Task PrimaryText_ClearsAA_OnEveryDarkSurface()
    {
        foreach (var surface in DarkSurfaces)
        {
            double ratio = Accessibility.ContrastRatio(TerminalColorPalette.Text, surface);
            await Assert.That(ratio).IsGreaterThanOrEqualTo(Accessibility.TextAaRatio)
                .Because($"text on {surface} = {ratio:F2}");
        }
    }

    [Test]
    public async Task RoleAccents_ClearLargeTextOrUI_Threshold_OnEveryDarkSurface()
    {
        foreach (var (color, name) in Accents)
        {
            foreach (var surface in DarkSurfaces)
            {
                double ratio = Accessibility.ContrastRatio(color, surface);
                await Assert.That(ratio).IsGreaterThanOrEqualTo(Accessibility.LargeTextAaRatio)
                    .Because($"{name} on {surface} = {ratio:F2} < {Accessibility.LargeTextAaRatio}");
            }
        }
    }

    [Test]
    public async Task FocusRing_AccentOnElevatedSurface_MeetsUiComponentRatio()
    {
        // Focus indicators must reach ≥3:1 wherever they can appear.
        await Assert.That(Accessibility.ContrastRatio(TerminalColorPalette.Accent, TerminalColorPalette.Surface))
            .IsGreaterThanOrEqualTo(Accessibility.UiComponentRatio);
        await Assert.That(Accessibility.ContrastRatio(TerminalColorPalette.Accent, TerminalColorPalette.Surface2))
            .IsGreaterThanOrEqualTo(Accessibility.UiComponentRatio);
    }

    /// <summary>
    /// Muted (#5C6773) is the HDS hint/glyph token — decorative tier. It
    /// clears the UI-component ratio only on the base surfaces; any secondary
    /// TEXT usage must sit on Panel/Bg at large-text size or move to a lifted
    /// tone. Guarded here so nobody reshuffles surfaces under it unnoticed.
    /// </summary>
    [Test]
    public async Task Muted_DocumentationContract_BaseSurfacesOnly()
    {
        await Assert.That(Accessibility.ContrastRatio(TerminalColorPalette.Muted, TerminalColorPalette.Background))
            .IsGreaterThanOrEqualTo(Accessibility.UiComponentRatio);
        await Assert.That(Accessibility.ContrastRatio(TerminalColorPalette.Muted, TerminalColorPalette.Panel))
            .IsGreaterThanOrEqualTo(Accessibility.UiComponentRatio);
        await Assert.That(Accessibility.ContrastRatio(TerminalColorPalette.Muted, TerminalColorPalette.Surface))
            .IsGreaterThanOrEqualTo(Accessibility.UiComponentRatio);
    }

    [Test]
    public void RatioScale_BoundedAndSymmetric()
    {
        var white = new RgbColor(255, 255, 255);
        var black = new RgbColor(0, 0, 0);

        const double Epsilon = 1e-9;
        if (Math.Abs(Accessibility.ContrastRatio(white, black) - 21.0) > Epsilon ||
            Math.Abs(Accessibility.ContrastRatio(black, black) - 1.0) > Epsilon ||
            Math.Abs(Accessibility.RelativeLuminance(white) - 1.0) > Epsilon ||
            Math.Abs(Accessibility.RelativeLuminance(black) - 0.0) > Epsilon)
        {
            throw new InvalidOperationException("WCAG math regression");
        }
    }
}
