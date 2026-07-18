using System.Collections.Immutable;
namespace Harbor.Ui.Framework.State;
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

    // ── panel actions ───────────────────────────────────────────────────
    /// <summary>Alt+1..Alt+9 — toggle the Nth registered panel. Slot index comes from the key's Character.</summary>
    TogglePanelSlot,

    /// <summary>Ctrl+Tab — cycle focus between visible panels and chat.</summary>
    CyclePanelFocus,

    /// <summary>Esc or 'q' while a panel is focused — return focus to chat.</summary>
    ClosePanel,

    /// <summary>Ctrl+Up / Ctrl+Right — grow the focused panel.</summary>
    ResizePanelGrow,

    /// <summary>Ctrl+Down / Ctrl+Left — shrink the focused panel.</summary>
    ResizePanelShrink,

    /// <summary>'?' — toggle the help / keymap panel.</summary>
    HelpPanel
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
    /// <summary>A panel owns focus; the specific panel id is in <c>UiState.FocusedPanelId</c>.</summary>
    Panel
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
