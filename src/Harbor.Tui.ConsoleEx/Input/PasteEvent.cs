namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>
/// A completed bracketed-paste block as ONE atomic text payload.
///
/// Anti-injection invariant: the content between the paste markers is copied
/// verbatim — embedded escape sequences and control bytes are NEVER decoded
/// into key/mouse events by the parser. Newlines inside a paste therefore can
/// never synthesize an Enter keypress on their own; submit remains an explicit
/// user action.
/// </summary>
/// <param name="text">Paste payload decoded as UTF-8 (invalid bytes → U+FFFD).</param>
/// <param name="wasTruncated">True when the payload exceeded the configured size cap
/// (paste-flood guard); the tail beyond the cap was dropped.</param>
public readonly struct PasteEvent(string text, bool wasTruncated)
{
    public string Text { get; } = text;
    public bool WasTruncated { get; } = wasTruncated;

    public override string ToString() =>
        $"Paste({Text.Length} chars{(WasTruncated ? ", truncated" : string.Empty)})";
}
