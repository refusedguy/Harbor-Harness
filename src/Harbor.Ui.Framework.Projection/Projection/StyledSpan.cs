using System.Diagnostics;
namespace Harbor.Ui.Framework.Projection;

[DebuggerDisplay("{Text,nq}")]
public readonly record struct StyledSpan(
    string Text,
    RgbColor? Foreground,
    RgbColor? Background,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Dim,
    UiSpanStyle? Style);

public readonly record struct RgbColor(
    byte R,
    byte G,
    byte B);