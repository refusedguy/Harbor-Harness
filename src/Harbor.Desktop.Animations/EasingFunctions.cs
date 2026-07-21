namespace Harbor.Desktop.Animations;
/// <summary>
///     Easing-function delegates and a registry of named easings. Each
///     platform app uses the <see cref="Apply" /> function to interpolate an
///     animation value; the named easing strings
///     (<see cref="EasingFunctions.CubicInOut" /> etc.) map to
///     <see cref="Harbor.Desktop.DesignSystem.AnimationTokens" /> for serialization.
/// </summary>
public static class EasingFunctions
{
    /// <summary>Linear easing — constant velocity.</summary>
    public static double Linear(double t) => t;

    /// <summary>Quadratic ease-in — starts slow, accelerates.</summary>
    public static double EaseIn(double t) => t * t;

    /// <summary>Quadratic ease-out — starts fast, decelerates.</summary>
    public static double EaseOut(double t) => t * (2 - t);

    /// <summary>Quadratic ease-in-out — slow-fast-slow.</summary>
    public static double EaseInOut(double t)
        => t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;

    /// <summary>Cubic ease-in-out — sharper slow-fast-slow.</summary>
    public static double CubicInOut(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>Quartic ease-out — fast start, very gentle landing.</summary>
    public static double QuarticOut(double t)
        => 1 - Math.Pow(1 - t, 4);

    /// <summary>Quintic ease-in-out — very pronounced slow-fast-slow.</summary>
    public static double QuinticInOut(double t)
        => t < 0.5 ? 16 * t * t * t * t * t : 1 - Math.Pow(-2 * t + 2, 5) / 2;

    /// <summary>Spring-like easing — overshoots slightly then settles.</summary>
    public static double Spring(double t)
    {
        // Critically-damped spring approximation.
        return 1 - Math.Cos(t * Math.PI * 0.5);
    }

    /// <summary>Resolve a named easing (from <see cref="AnimationTokens" />) to a delegate.</summary>
    /// <param name="name">Easing name (e.g. "cubicInOut").</param>
    /// <returns>The matching <see cref="Func{T, TResult}" />, or <see cref="Linear" /> if unknown.</returns>
    public static Func<double, double> Resolve(string name) => name switch
    {
        AnimationTokens.EasingLinear => Linear,
        AnimationTokens.EasingEaseIn => EaseIn,
        AnimationTokens.EasingEaseOut => EaseOut,
        AnimationTokens.EasingEaseInOut => EaseInOut,
        AnimationTokens.EasingCubicInOut => CubicInOut,
        AnimationTokens.EasingQuarticOut => QuarticOut,
        AnimationTokens.EasingSpring => Spring,
        _ => Linear
    };

    /// <summary>Sample <paramref name="easing" /> at <paramref name="progress" /> (0..1).</summary>
    public static double Apply(Func<double, double> easing, double progress)
        => easing(Math.Clamp(progress, 0.0, 1.0));
}
