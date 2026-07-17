namespace Harbor.Tui.Abstractions.State;

/// <summary>
///     All interactive actions the chat UI understands, decoupled from the raw key
///     that triggers them. The concrete key bindings live in <see cref="ChatKeyMap" />
///     so they are documented and rendered from one place, shared by every renderer.
/// </summary>
public enum ChatAction
{
    None,
    Quit,
    Abort,
    Submit,
    ToggleFocus,
    ScrollUpLine,
    ScrollDownLine,
    ScrollUpPage,
    ScrollDownPage,
    ScrollTop,
    ScrollBottom,
    InputHistoryPrev,
    InputHistoryNext,
    Autocomplete,
    Backspace,
    Clear,
    Char,
}

/// <summary>
///     Focus owner within the chat screen. Drives which region receives keystrokes
///     and how the UI is highlighted. Lives in the shared model so it survives
///     replay/time-travel and is identical across renderers.
/// </summary>
public enum FocusMode
{
    Input,
    Chat,
}
