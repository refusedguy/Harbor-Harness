namespace Harbor.Desktop.DesignSystem;
/// <summary>
///     Animation tokens — durations and easing-curve names shared by every
///     desktop app. The actual easing-function implementations live in
///     <c>Harbor.Desktop.Animations.EasingFunctions</c>.
/// </summary>
public static class AnimationTokens
{
    // ── Durations (ms) ─────────────────────────────────────────────────────
    public const int InstantMs = 0;
    public const int FastMs = 150;
    public const int NormalMs = 300;
    public const int SlowMs = 500;
    public const int SlowerMs = 800;

    // ── Easing curve names (resolved by Harbor.Desktop.Animations) ─────────
    public const string EasingLinear = "linear";
    public const string EasingEaseIn = "easeIn";
    public const string EasingEaseOut = "easeOut";
    public const string EasingEaseInOut = "easeInOut";
    public const string EasingCubicInOut = "cubicInOut";
    public const string EasingQuarticOut = "quarticOut";
    public const string EasingSpring = "spring";

    // ── Default fade / slide / scale durations ─────────────────────────────
    public const int FadeDurationMs = FastMs;
    public const int SlideDurationMs = NormalMs;
    public const int ScaleDurationMs = FastMs;
    public const int ToastDurationMs = NormalMs;
    public const int PaletteDurationMs = FastMs;
}
