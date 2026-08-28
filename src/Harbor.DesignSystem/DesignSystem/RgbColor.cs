namespace Harbor.Ui.Framework.Projection;

/// <summary>
/// Canonical sRGB color primitive of the Harbor design system — the value
/// every palette, theme record, and projection span carries. 24-bit truecolor,
/// one byte per channel.
///
/// Defined in the <c>Harbor.DesignSystem</c> assembly (the standalone,
/// zero-dependency design-system package) but kept in the
/// <c>Harbor.Ui.Framework.Projection</c> namespace for binary/source
/// compatibility: every existing consumer resolves the type exactly as before,
/// while the design-system package no longer depends on any Harbor assembly.
/// </summary>
public readonly record struct RgbColor(
    byte R,
    byte G,
    byte B);
