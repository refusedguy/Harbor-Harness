namespace Harbor.Tui.CellForge.Input;

/// <summary>Kind of a terminal capability probe response.</summary>
public enum CapabilityEventKind : byte
{
    /// <summary>Answer to the kitty keyboard query: <c>CSI ? flags u</c>.</summary>
    KittyFlagsReport = 0,

    /// <summary>DECRQM answer: <c>CSI ? mode ; value $ y</c>.</summary>
    DecRqmReport = 1,

    /// <summary>Primary device attributes answer: <c>CSI [?] params c</c>.</summary>
    DeviceAttributes = 2,

    /// <summary>Cursor position report: <c>CSI row ; col R</c>.</summary>
    /// <summary>Cursor position report: <c>CSI row ; col R</c>.</summary>
    CursorPositionReport = 3,

    /// <summary>OSC 11 background-color answer: <c>OSC 11 ; rgb:RR/GG/BB ST|BEL</c>
    /// — the auto-theme probe response (widgets §3.x).</summary>
    Osc11BackgroundReport = 4,

    /// <summary>kitty desktop-notification capability answer: <c>OSC 99 ; i=… :
    /// p=&lt;payload types&gt; … ST|BEL</c> — notifications supported (osc-sprint §777).</summary>
    Osc99NotifyReport = 5,
}

/// <summary>A capability-probe response intercepted by the parser. Probe
/// traffic is routed here, never surfaced as user input.</summary>
public readonly struct CapabilityEvent(
    CapabilityEventKind kind,
    uint flags,
    int mode,
    int value,
    int row,
    int column,
    int red = 0,
    int green = 0,
    int blue = 0)
{
    public CapabilityEventKind Kind { get; } = kind;
    public uint Flags { get; } = flags;
    public int Mode { get; } = mode;
    public int Value { get; } = value;
    public int Row { get; } = row;
    public int Column { get; } = column;

    /// <summary>Background report color channels (0..255) — valid for <see cref="CapabilityEventKind.Osc11BackgroundReport" />.</summary>
    public int Red { get; } = red;
    public int Green { get; } = green;
    public int Blue { get; } = blue;

    public static CapabilityEvent KittyFlags(uint flags) => new(CapabilityEventKind.KittyFlagsReport, flags, 0, 0, 0, 0);
    public static CapabilityEvent DecRqm(int mode, int value) => new(CapabilityEventKind.DecRqmReport, 0, mode, value, 0, 0);
    public static CapabilityEvent Da(int firstParam) => new(CapabilityEventKind.DeviceAttributes, 0, firstParam, 0, 0, 0);
    public static CapabilityEvent CursorPosition(int row, int column) => new(CapabilityEventKind.CursorPositionReport, 0, 0, 0, row, column);
    public static CapabilityEvent Osc11Background(int red, int green, int blue) =>
        new(CapabilityEventKind.Osc11BackgroundReport, 0, 0, 0, 0, 0, red, green, blue);

    public static CapabilityEvent Osc99Notify() => new(CapabilityEventKind.Osc99NotifyReport, 0, 0, 0, 0, 0);
}
