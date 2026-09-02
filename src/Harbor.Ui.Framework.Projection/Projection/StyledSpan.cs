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

// RgbColor moved to the standalone Harbor.DesignSystem package; the
// namespace is preserved so all consumers resolve it unchanged.