namespace Harbor.Desktop.Animations;
/// <summary>
///     Fade transition — opacity 0 ↔ 1. The simplest transition; used by
///     toasts, the command palette, and modal dialogs.
/// </summary>
/// <param name="Duration">Duration of the fade. Defaults to <see cref="AnimationDurations.Fade" />.</param>
/// <param name="EasingName">Easing-curve name. Defaults to <see cref="AnimationTokens.EasingEaseOut" />.</param>
public sealed record FadeTransition(
    TimeSpan Duration,
    string EasingName = AnimationTokens.EasingEaseOut)
{
    /// <summary>Construct a <see cref="FadeTransition" /> with the default duration.</summary>
    public FadeTransition() : this(AnimationDurations.Fade) { }
}

/// <summary>
///     Slide transition — translate from (offsetX, offsetY) to (0, 0).
///     Used by the sidebar (slide-in), the command palette (slide-down), and
///     page transitions.
/// </summary>
/// <param name="Duration">Duration of the slide. Defaults to <see cref="AnimationDurations.Slide" />.</param>
/// <param name="OffsetX">Starting X offset in px. Positive = slides in from right.</param>
/// <param name="OffsetY">Starting Y offset in px. Positive = slides in from bottom.</param>
/// <param name="EasingName">Easing-curve name. Defaults to <see cref="AnimationTokens.EasingCubicInOut" />.</param>
public sealed record SlideTransition(
    TimeSpan Duration,
    double OffsetX,
    double OffsetY,
    string EasingName = AnimationTokens.EasingCubicInOut)
{
    /// <summary>Construct a <see cref="SlideTransition" /> with the default duration and zero offsets.</summary>
    public SlideTransition() : this(AnimationDurations.Slide, 0, 0) { }
}

/// <summary>
///     Scale transition — scale from <see cref="FromScale" /> to 1.0. Used by
///     modal dialogs (pop-in effect) and the command palette.
/// </summary>
/// <param name="Duration">Duration of the scale. Defaults to <see cref="AnimationDurations.Scale" />.</param>
/// <param name="FromScale">Starting scale (0.0 to 1.0). Defaults to 0.95.</param>
/// <param name="EasingName">Easing-curve name. Defaults to <see cref="AnimationTokens.EasingSpring" />.</param>
public sealed record ScaleTransition(
    TimeSpan Duration,
    double FromScale = 0.95,
    string EasingName = AnimationTokens.EasingSpring)
{
    /// <summary>Construct a <see cref="ScaleTransition" /> with the default duration.</summary>
    public ScaleTransition() : this(AnimationDurations.Scale) { }
}

/// <summary>
///     Color transition — animate from <see cref="FromColor" /> to
///     <see cref="ToColor" />. Used by the theme switcher (smooth color fade).
/// </summary>
/// <param name="Duration">Duration of the color fade. Defaults to <see cref="AnimationDurations.Normal" />.</param>
/// <param name="FromColor">Starting color.</param>
/// <param name="ToColor">Target color.</param>
public sealed record ColorTransition(
    TimeSpan Duration,
    RgbColor FromColor,
    RgbColor ToColor)
{
    /// <summary>Interpolate the color at progress <paramref name="t" /> (0..1).</summary>
    public RgbColor Interpolate(double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        byte r = (byte)(FromColor.R + (ToColor.R - FromColor.R) * clamped);
        byte g = (byte)(FromColor.G + (ToColor.G - FromColor.G) * clamped);
        byte b = (byte)(FromColor.B + (ToColor.B - FromColor.B) * clamped);
        return new RgbColor(r, g, b);
    }
}
