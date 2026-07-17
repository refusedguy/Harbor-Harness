using System.Collections.Immutable;
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
    Char
}

/// <summary>
///     Focus owner within the chat screen. Drives which region receives keystrokes
///     and how the UI is highlighted. Lives in the shared model so it survives
///     replay/time-travel and is identical across renderers.
/// </summary>
public enum FocusMode
{
    Input,
    Chat
}

/// <summary>
///     Shared command vocabulary for the interactive chat. Lives in abstractions so
///     the pure reducer (autocomplete) and the effect host (slash dispatch) read from
///     one source of truth instead of a concrete host type.
/// </summary>
public static class ChatCommands
{
    /// <summary>Slash commands offered by autocomplete and accepted as input.</summary>
    public static readonly ImmutableArray<string> Slash = ImmutableArray.Create(
        "/help", "/exit", "/setup", "/auth", "/model", "/agent", "/config",
        "/providers", "/sessions", "/tui", "/storage", "/clear");

    /// <summary>Words that quit the interactive loop when submitted as input.</summary>
    public static readonly ImmutableHashSet<string> ExitWords =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "exit", "quit", ":q");
}
