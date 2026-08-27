namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// HDS v1 spring-physics animator for panel resizing (Bubble Tea "harmonica"
/// model): a damped harmonic oscillator advanced in discrete per-frame steps
/// at the 60 fps display cadence. Deterministic — every <see cref="Step" /> is a
/// pure function of the previous state, so tests and golden frames can pin
/// ticks instead of wall-clock time. Zero allocations; one instance per
/// animated quantity (split ratio, panel height, …).
/// </summary>
public sealed class SpringFx
{
    /// <summary>Default per-frame stiffness — settles in ≈300 ms (HDS NormalMs) with a light overshoot.</summary>
    public const double DefaultStiffness = 0.28;

    /// <summary>Default damping ratio — 0.5 gives the harmonica spring feel (~11 % overshoot) without ringing.</summary>
    public const double DefaultDampingRatio = 0.5;

    /// <summary>Position/velocity tolerance for the settled snap.</summary>
    internal const double Epsilon = 0.001;

    private readonly double _stiffness;
    private readonly double _damping;

    /// <summary>Current animated value.</summary>
    public double Position { get; private set; }

    /// <summary>Per-frame velocity (units per frame).</summary>
    public double Velocity { get; private set; }

    /// <summary>Value the spring moves toward.</summary>
    public double Target { get; private set; }

    /// <summary>True once the spring is within snap distance of its target and at rest.</summary>
    public bool Settled { get; private set; } = true;

    /// <summary>Creates a spring at rest on <paramref name="initial" />.</summary>
    public SpringFx(double initial, double stiffness = DefaultStiffness, double dampingRatio = DefaultDampingRatio)
    {
        _stiffness = stiffness;
        _damping = 2.0 * Math.Sqrt(stiffness) * dampingRatio;
        Position = initial;
        Target = initial;
    }

    /// <summary>Retargets the spring; motion starts on the next <see cref="Step" />.</summary>
    public void Retarget(double target)
    {
        if (Math.Abs(Target - target) < Epsilon)
        {
            return;
        }

        Target = target;
        Settled = false;
    }

    /// <summary>Teleports to <paramref name="value" /> with zero velocity — no animation.</summary>
    public void SnapTo(double value)
    {
        Position = value;
        Target = value;
        Velocity = 0;
        Settled = true;
    }

    /// <summary>Advances the simulation by one frame. Returns <see cref="Position" />.</summary>
    public double Step()
    {
        if (Settled)
        {
            return Position;
        }

        // Semi-implicit Euler in per-frame units: damping first, then integrate.
        Velocity += (-_stiffness * (Position - Target)) - (_damping * Velocity);
        Position += Velocity;

        if (Math.Abs(Position - Target) < Epsilon && Math.Abs(Velocity) < Epsilon)
        {
            Position = Target;
            Velocity = 0;
            Settled = true;
        }

        return Position;
    }
}
