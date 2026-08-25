namespace Harbor.Tui.ConsoleEx.Capabilities;

/// <summary>Result of terminal capability probing (design §2.4/§6).</summary>
public readonly struct TerminalCapabilities
{
    /// <summary>True once a probe cycle completed (either answer or timeout).</summary>
    public bool Probed { get; init; }

    /// <summary>Kitty keyboard protocol confirmed via CSI ? u answer.</summary>
    public bool Kitty { get; init; }

    /// <summary>Flags the terminal reported as currently pushed.</summary>
    public uint KittyFlags { get; init; }

    /// <summary>Terminal answered a DECRQM/DA probe — VT-aware but kitty-less.</summary>
    public bool VtResponsive { get; init; }

    /// <summary>DECRQM confirmed bracketed-paste mode (value set/reset known).</summary>
    public bool BracketedPasteConfirmed { get; init; }

    /// <summary>
    /// DECRQM confirmed synchronized-output mode 2026 (celldiff §3.4) — frames
    /// may be wrapped in CSI ?2026 h … l for atomic application.
    /// </summary>
    public bool SyncUpdates { get; init; }

    public static TerminalCapabilities Unprobed() => default;
}
