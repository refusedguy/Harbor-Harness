namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>
/// Key lifecycle phase. Press/repeat/release distinction is only available
/// when the kitty protocol reports event types (flag 2); legacy terminals
/// always produce <see cref="KeyEventType.Press"/>.
/// </summary>
public enum KeyEventType : byte
{
    Press = 0,
    Repeat = 1,
    Release = 2,
}
