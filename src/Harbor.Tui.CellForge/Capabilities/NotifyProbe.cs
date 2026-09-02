namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>Desktop-notification transports the runtime can speak.</summary>
public enum DesktopNotifyKind : byte
{
    /// <summary>Terminal gave no signal — notifications stay suppressed.</summary>
    None = 0,

    /// <summary>kitty OSC 99 — confirmed by the capability probe answer
    /// (<c>CapabilityEventKind.Osc99NotifyReport</c>).</summary>
    Osc99 = 1,

    /// <summary>rxvt-unicode family OSC 777 — env-detected, no wire probe
    /// exists for this family.</summary>
    Osc777 = 2,
}

/// <summary>
/// Desktop-notification family detection (osc-sprint §777). kitty is confirmed
/// on the wire via the OSC 99 probe; the urxvt family (OSC 777) has no probe
/// mechanism, so it is env-detected with the same discipline as
/// <see cref="InlineImageProbe" />: explicit override first, then heuristics.
/// </summary>
public static class NotifyProbe
{
    private const string OverrideVar = "HARBOR_OSC777";

    /// <summary>Detects the OSC 777 family for the current session.</summary>
    public static DesktopNotifyKind Detect(Func<string, string?>? environmentLookup = null)
    {
        var lookup = environmentLookup ?? Environment.GetEnvironmentVariable;
        string? overrideValue = lookup(OverrideVar);
        if (overrideValue == "1" || string.Equals(overrideValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            return DesktopNotifyKind.Osc777;
        }

        if (overrideValue == "0" || string.Equals(overrideValue, "false", StringComparison.OrdinalIgnoreCase))
        {
            return DesktopNotifyKind.None;
        }

        string? term = lookup("TERM");
        return term is not null
               && (term.Contains("rxvt", StringComparison.OrdinalIgnoreCase)
                   || term.Contains("urxvt", StringComparison.OrdinalIgnoreCase))
            ? DesktopNotifyKind.Osc777
            : DesktopNotifyKind.None;
    }
}
