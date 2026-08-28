namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>
/// Escape-sequence writers for terminal mode control (design §2.2/§3.1/§4).
/// Static formatters ONLY — nothing here touches Console directly (lesson
/// from AnsiTui's Ansi class); callers write the returned strings themselves.
/// </summary>
public static class TerminalQueries
{
    // ── Kitty keyboard protocol ───────────────────────────────────────────

    /// <summary>Query currently-pushed kitty flags: CSI ? u (answer: CSI ? flags u).</summary>
    public const string KittyQuery = "\u001B[?u";

    /// <summary>Stacked push of kitty protocol flags: CSI &gt; flags u.</summary>
    public static string KittyPush(uint flags) => $"\u001B[>{flags}u";

    /// <summary>Pop one level of kitty flags: CSI &lt; u.</summary>
    public const string KittyPop = "\u001B[<u";

    /// <summary>MVP flag set: disambiguate escape codes only (§2.2).</summary>
    public const uint KittyDisambiguateFlags = 1;

    // ── DECRQM ────────────────────────────────────────────────────────────

    /// <summary>DECRQM request for bracketed-paste mode support: CSI ? Pd $ p
    /// (answer: CSI ? Ps ; Pv $ y). Used as the fallback probe when the kitty
    /// query stays silent (§2.4).</summary>
    public const string DecRqmBracketedPaste = "\u001B[?2004$p";

    /// <summary>DECRQM response mode number for bracketed paste.</summary>
    public const int BracketedPasteMode = 2004;

    /// <summary>DECRQM request for synchronized-output mode support:
    /// CSI ? 2026 $ p (answer: CSI ? 2026 ; Pv $ y) — celldiff §3.4.</summary>
    public const string DecRqmSyncUpdates = "\u001B[?2026$p";

    /// <summary>DECRQM response mode number for synchronized output.</summary>
    public const int SyncUpdatesMode = 2026;

    // ── SGR mouse ─────────────────────────────────────────────────────────

    /// <summary>Enable click tracking + SGR encoding: CSI ?1000h CSI ?1006h (§3.1).</summary>
    public const string MouseClickEnable = "\u001B[?1000h\u001B[?1006h";

    /// <summary>Enable drag tracking additionally: CSI ?1002h (v2 panel resize).</summary>
    public const string MouseDragEnable = "\u001B[?1002h";

    /// <summary>Disable all mouse tracking in reverse-enable order.</summary>
    public const string MouseDisable = "\u001B[?1006l\u001B[?1002l\u001B[?1000l";

    // ── Bracketed paste ───────────────────────────────────────────────────

    public const string PasteEnable = "\u001B[?2004h";
    public const string PasteDisable = "\u001B[?2004l";

    // ── OSC 11 background-color report (auto-theme) ───────────────────────

    /// <summary>Query the terminal's background color: OSC 11 ; ? BEL.
    /// Feed the raw response to Harbor.DesignSystem's
    /// TerminalBackgroundProbe.Detect for theme auto-picking.</summary>
    public const string Osc11BackgroundQuery = "\u001B]11;?\u0007";
}
