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

        if (Math.Abs(Accessibility.ContrastRatio(white, black) - 21.0) > 1e-9 ||
            Math.Abs(Accessibility.ContrastRatio(black, black) - 1.0) > 1e-9 ||
            Math.Abs(Accessibility.RelativeLuminance(white) - 1.0) > 1e-9 ||
            Math.Abs(Accessibility.RelativeLuminance(black) - 0.0) > 1e-9)
        {
            throw new InvalidOperationException("WCAG math regression");
        }
    }

    // ── Avalonia desktop theme tokens (#433) ─────────────────────────────
    // Measured, not asserted: the foreground/background pairs
    // docs/ACCESSIBILITY.md §2.3–2.4 claims for the Catppuccin Mocha (dark)
    // and Latte (light) dictionaries in apps/Harbor.App.Avalonia/Themes/.
    // Hex literals are pinned to the token values in {Dark,Light}.axaml so a
    // palette edit that silently breaks readability fails CI. Pure math —
    // no Avalonia runtime, no headless session.

    private static RgbColor Hex(string h) => new(
        Convert.ToByte(h.Substring(1, 2), 16),
        Convert.ToByte(h.Substring(3, 2), 16),
        Convert.ToByte(h.Substring(5, 2), 16));

    [Test]
    public async Task AvaloniaDark_BodyText_ClearsAA()
    {
        var @base = Hex("#1E1E2E"); // MochaBase
        foreach (var (fg, name) in new[] { ("#CDD6F4", "MochaText"), ("#A6ADC8", "MochaSubtext0") })
        {
            double ratio = Accessibility.ContrastRatio(Hex(fg), @base);
            await Assert.That(ratio).IsGreaterThanOrEqualTo(Accessibility.TextAaRatio)
                .Because($"{name} on MochaBase = {ratio:F2}");
        }
    }

    [Test]
    public async Task AvaloniaDark_LargeTextAndUiTier_Clears3To1()
    {
        var @base = Hex("#1E1E2E"); // MochaBase
        // MochaOverlay2 (secondary text) and Crust-on-Blue (text on accent).
        foreach (var (fg, bg, name) in new[] { ("#9399B2", "#1E1E2E", "MochaOverlay2"), ("#11111B", "#89B4FA", "MochaCrust-on-MochaBlue") })
        {
            double ratio = Accessibility.ContrastRatio(Hex(fg), Hex(bg));
            await Assert.That(ratio).IsGreaterThanOrEqualTo(Accessibility.LargeTextAaRatio)
                .Because($"{name} = {ratio:F2}");
        }
    }

    [Test]
    public async Task AvaloniaLight_BodyText_ClearsAA()
    {
        var @base = Hex("#EFF1F5"); // LatteBase
        double text = Accessibility.ContrastRatio(Hex("#4C4F69"), @base); // LatteText
        await Assert.That(text).IsGreaterThanOrEqualTo(Accessibility.TextAaRatio)
            .Because($"LatteText on LatteBase = {text:F2}");
        double accentText = Accessibility.ContrastRatio(Hex("#FFFFFF"), Hex("#1E66F5")); // White on LatteBlue
        await Assert.That(accentText).IsGreaterThanOrEqualTo(Accessibility.TextAaRatio)
            .Because($"White on LatteBlue = {accentText:F2}");
    }

    /// <summary>
    /// Subtext-tier documentation contract: LatteSubtext0 (4.37:1) and
    /// MochaOverlay0 (3.36:1) clear the ≥3:1 large-text/UI-component tier but
    /// are BELOW the 4.5:1 body-text tier — they must never be used for
    /// normal-size body copy. The strict-less-than assertions prove the gate
    /// discriminates: these pairs would fail if they were moved into a
    /// body-text test above.
    /// </summary>
    [Test]
    public async Task Avalonia_SubtextTier_DocumentationContract()
    {
        double latte = Accessibility.ContrastRatio(Hex("#6C6F85"), Hex("#EFF1F5"));
        await Assert.That(latte).IsGreaterThanOrEqualTo(Accessibility.LargeTextAaRatio)
            .Because($"LatteSubtext0 on LatteBase = {latte:F2}");
        await Assert.That(latte < Accessibility.TextAaRatio).IsTrue()
            .Because($"LatteSubtext0 on LatteBase = {latte:F2} is not body-text tier");

        double mocha = Accessibility.ContrastRatio(Hex("#6C7086"), Hex("#1E1E2E"));
        await Assert.That(mocha).IsGreaterThanOrEqualTo(Accessibility.UiComponentRatio)
            .Because($"MochaOverlay0 on MochaBase = {mocha:F2}");
        await Assert.That(mocha < Accessibility.TextAaRatio).IsTrue()
            .Because($"MochaOverlay0 on MochaBase = {mocha:F2} is not body-text tier");
    }
}
