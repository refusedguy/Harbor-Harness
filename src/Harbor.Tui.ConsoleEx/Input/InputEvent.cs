namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>Discriminator for the <see cref="InputEvent"/> union.</summary>
public enum InputEventKind : byte
{
    None = 0,
    Key = 1,
    Mouse = 2,
    Paste = 3,
    Resize = 4,
    Capability = 5,

    /// <summary>A malformed/unparseable sequence was discarded (resync signal
    /// for diagnostics; carries no payload).</summary>
    Unknown = 6,
}

/// <summary>
/// Typed terminal input event (struct union). Produced by
/// <see cref="Parsing.EscapeSequenceParser"/>; zero-allocation on the
/// key/mouse/resize paths — only <see cref="Paste"/> carries a heap string.
/// </summary>
public readonly struct InputEvent
{
    private InputEvent(InputEventKind kind, KeyEvent key, MouseEvent mouse, PasteEvent paste, ResizeSignal resize, CapabilityEvent capability)
    {
        Kind = kind;
        Key = key;
        Mouse = mouse;
        Paste = paste;
        Resize = resize;
        Capability = capability;
    }

    public InputEventKind Kind { get; }
    public KeyEvent Key { get; }
    public MouseEvent Mouse { get; }
    public PasteEvent Paste { get; }
    public ResizeSignal Resize { get; }
    public CapabilityEvent Capability { get; }

    public static InputEvent FromKey(KeyEvent evt) => new(InputEventKind.Key, evt, default, default, default, default);
    public static InputEvent FromMouse(MouseEvent evt) => new(InputEventKind.Mouse, default, evt, default, default, default);
    public static InputEvent FromPaste(PasteEvent evt) => new(InputEventKind.Paste, default, default, evt, default, default);
    public static InputEvent FromResize(ResizeSignal evt) => new(InputEventKind.Resize, default, default, default, evt, default);
    public static InputEvent FromCapability(CapabilityEvent evt) => new(InputEventKind.Capability, default, default, default, default, evt);
    public static InputEvent Unknown() => new(InputEventKind.Unknown, default, default, default, default, default);

    public override string ToString() => Kind switch
    {
        InputEventKind.Key => Key.ToString(),
        InputEventKind.Mouse => Mouse.ToString(),
        InputEventKind.Paste => Paste.ToString(),
        InputEventKind.Resize => Resize.ToString(),
        InputEventKind.Capability => $"Capability({Capability.Kind})",
        InputEventKind.Unknown => "Unknown(malformed sequence)",
        _ => "None",
    };
}
