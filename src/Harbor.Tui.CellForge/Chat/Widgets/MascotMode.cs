namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Where the mascot lives (sprint mascot-brand T2).</summary>
public enum MascotMode : byte
{
    /// <summary>One trailing-edge row inside the status footer (default).</summary>
    Footer = 0,

    /// <summary>A dedicated 3-row cat panel beside the composer.</summary>
    Panel,

    /// <summary>No mascot at all.</summary>
    Off,
}

/// <summary>
/// Env-resolved mascot mode — read once per process, never per frame.
/// <c>HARBOR_MASCOT=off</c> stays the hard kill-switch used by CI / golden
/// tests: it wins over <c>HARBOR_MASCOT_MODE</c> and disables everything.
/// </summary>
public static class MascotModeEnv
{
    /// <summary>Resolved once at type init; render paths only read this field.</summary>
    public static readonly MascotMode Value = Resolve(
        Environment.GetEnvironmentVariable("HARBOR_MASCOT"),
        Environment.GetEnvironmentVariable("HARBOR_MASCOT_MODE"));

    /// <summary>Pure resolver (unit-testable): unknown values fall back to the footer default.</summary>
    internal static MascotMode Resolve(string? mascot, string? mode)
    {
        if (string.Equals(mascot, "off", StringComparison.OrdinalIgnoreCase))
        {
            return MascotMode.Off;
        }

        return mode?.Trim().ToLowerInvariant() switch
        {
            "off" => MascotMode.Off,
            "panel" => MascotMode.Panel,
            _ => MascotMode.Footer,
        };
    }
}
