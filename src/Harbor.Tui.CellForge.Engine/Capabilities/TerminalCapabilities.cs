namespace Harbor.Tui.CellForge.Capabilities;

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

    /// <summary>
    /// OSC 8 hyperlinks confirmed by <see cref="Osc8HyperlinkProbe"/> (issue
    /// #2217). No wire query exists for OSC 8, so this is an environmental
    /// result composed into the probe output; when false, link escape bytes
    /// are suppressed and only the visible text is emitted.
    /// </summary>
    public bool Osc8Hyperlinks { get; init; }

    /// <summary>OSC 7501 program status (issue #2219), environment-detected.</summary>
    public bool Osc7501 { get; init; }

    /// <summary>OSC 9;4 progress (issue #2219), environment-detected.</summary>
    public bool Osc94Progress { get; init; }

    /// <summary>OSC 7 working-directory report (issue #2219), environment-detected.</summary>
    public bool Osc7Cwd { get; init; }

    /// <summary>DECSCUSR cursor shape (issue #2219), environment-detected.</summary>
    public bool Decscusr { get; init; }

    /// <summary>Underline styles/color (issue #2219), environment-detected.</summary>
    public bool UnderlineStyles { get; init; }

    public static TerminalCapabilities Unprobed() => default;
}
