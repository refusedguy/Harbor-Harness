namespace Harbor.Tui.ConsoleEx.Parsing;

/// <summary>
/// States of the escape-sequence state machine (design §5.3).
/// UTF-8 multibyte tails are tracked inside <see cref="Ground"/> by the
/// incremental decoder rather than as a dedicated protocol state.
/// </summary>
public enum ParserState : byte
{
    /// <summary>Plain bytes: controls, printable characters, UTF-8 text.</summary>
    Ground = 0,

    /// <summary>ESC received — awaiting the sequence introducer.</summary>
    Escape,

    /// <summary>ESC O received — legacy SS3 one-byte-final sequences.</summary>
    Ss3,

    /// <summary>ESC [ received — awaiting parameters/intermediates/final.</summary>
    CsiEntry,

    /// <summary>Collecting CSI parameter bytes (0x30–0x3F).</summary>
    CsiParam,

    /// <summary>Collecting CSI intermediate bytes (0x20–0x2F).</summary>
    CsiIntermediate,

    /// <summary>Malformed CSI — swallowing until the final byte.</summary>
    CsiIgnore,

    /// <summary>OSC string (ESC ]) — until BEL or ST (ESC \).</summary>
    OscString,

    /// <summary>DCS/APC/PM/SOS string — until ST (ESC \).</summary>
    StringUntilSt,

    /// <summary>Inside bracketed paste — everything literal until ESC [ 201 ~.</summary>
    PastePayload,
}
