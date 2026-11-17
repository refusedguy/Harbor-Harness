namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Terminal focus transition reported through CSI ?1004h (issue #2218):
/// <c>CSI I</c> means the window gained focus, <c>CSI O</c> means it lost
/// focus. The typed shape mirrors Textual's AppFocus/AppBlur so the host can
/// pause frame-bound animation without parsing escape bytes itself.
/// </summary>
public readonly struct FocusEvent(bool isFocused)
{
    /// <summary>True on focus gain, false on focus loss.</summary>
    public bool IsFocused { get; } = isFocused;

    /// <summary>The terminal window regained focus.</summary>
    public static readonly FocusEvent Gained = new(true);

    /// <summary>The terminal window lost focus.</summary>
    public static readonly FocusEvent Lost = new(false);
}
