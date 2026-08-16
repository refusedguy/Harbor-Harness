namespace Harbor.Desktop.Animations;
/// <summary>
///     Standard animation durations shared by every desktop app. Mirror of
///     <see cref="AnimationTokens" /> durations
///     (re-exported as <see cref="TimeSpan" /> for use in platform animation
///     APIs that take a TimeSpan rather than an int ms).
/// </summary>
public static class AnimationDurations
{
    /// <summary>Instant — no animation.</summary>
    public static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(AnimationTokens.InstantMs);

    /// <summary>Fast — 150ms. Used for hover, button presses, palette open.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(AnimationTokens.FastMs);

    /// <summary>Normal — 300ms. Default for slide, scale, theme switch.</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(AnimationTokens.NormalMs);

    /// <summary>Slow — 500ms. Used for major page transitions.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(AnimationTokens.SlowMs);

    /// <summary>Slower — 800ms. Used for first-launch hero animations.</summary>
    public static readonly TimeSpan Slower = TimeSpan.FromMilliseconds(AnimationTokens.SlowerMs);

    /// <summary>Default fade duration.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(AnimationTokens.FadeDurationMs);

    /// <summary>Default slide duration.</summary>
    public static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(AnimationTokens.SlideDurationMs);

    /// <summary>Default scale duration.</summary>
    public static readonly TimeSpan Scale = TimeSpan.FromMilliseconds(AnimationTokens.ScaleDurationMs);

    /// <summary>Default toast duration.</summary>
    public static readonly TimeSpan Toast = TimeSpan.FromMilliseconds(AnimationTokens.ToastDurationMs);

    /// <summary>Default command-palette open/close duration.</summary>
    public static readonly TimeSpan Palette = TimeSpan.FromMilliseconds(AnimationTokens.PaletteDurationMs);
}
