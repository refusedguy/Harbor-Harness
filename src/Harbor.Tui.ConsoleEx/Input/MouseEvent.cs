namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>Mouse button identity. Numeric values are wire-id + 1
/// (xterm button bits 0/1/2 → Left/Middle/Right).</summary>
public enum MouseButton : byte
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 3,
}

/// <summary>
/// Mouse interaction kind. Click = clean press→release without motion;
/// Drag = motion while a button is held; Wheel = scroll wheel ticks.
/// Double-click detection is a consumer-side timing heuristic (terminal
/// protocols do not report it).
/// </summary>
public enum MouseEventType : byte
{
    Press = 0,
    Release = 1,
    Click = 2,
    Drag = 3,
    WheelUp = 4,
    WheelDown = 5,
}

/// <summary>A decoded SGR-mouse event. Coordinates are zero-based columns/rows
/// (wire values are one-based). Values outside the viewport are passed through
/// unclamped — consumers clamp before indexing (release-after-drag can land
/// beyond the window edge).</summary>
public readonly struct MouseEvent(
    MouseEventType type,
    MouseButton button,
    int column,
    int row,
    KeyModifiers modifiers)
{
    public MouseEventType Type { get; } = type;
    public MouseButton Button { get; } = button;
    public int Column { get; } = column;
    public int Row { get; } = row;
    public KeyModifiers Modifiers { get; } = modifiers;

    public override string ToString() => $"{Type} {Button} @({Column},{Row}) [{Modifiers}]";
}
