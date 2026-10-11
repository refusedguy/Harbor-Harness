using System.Collections.Immutable;
using Harbor.Ui.Framework.Commands;
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
    /// <summary>Shift+Enter / Alt+Enter — insert a newline instead of submitting.</summary>
    InsertNewline,
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
    HelpPanel,

    /// <summary>F12 — toggle the in-TUI diagnostics / logs panel (shows live ILogger output).</summary>
    ToggleLogsPanel,

    /// <summary>Ctrl+J (or a bare LF on terminals that send it instead) — open the worktree jump palette.</summary>
    JumpPalette,

    // ── tab-strip actions (#389, slice 2/3 — key layer for #388's state model) ──
    // Every one of these is a thin alias for an existing UiMsg tab transition
    // (CycleNextTab / CyclePreviousTab / CloseTab / OpenTab): the keymap owns
    // "which key", the reducer owns "what it means", the host owns "switch the
    // session". No renderer-private key branches (see ChatKeyMap).

    /// <summary>
    ///     Ctrl+Tab — focus the next open tab. Guarded by the keymap: it only
    ///     wins over <see cref="CyclePanelFocus" /> while a multi-tab strip is
    ///     actually open, so the panel-cycling binding keeps working otherwise.
    /// </summary>
    NextTab,

    /// <summary>Ctrl+Shift+Tab — focus the previous open tab (wraps).</summary>
    PreviousTab,

    /// <summary>
    ///     Ctrl+W — close the focused tab. Deliberately NOT the app: the
    ///     explicit quit path stays <see cref="Quit" /> (Esc / Ctrl+C ×2).
    /// </summary>
    CloseTab,

    /// <summary>
    ///     Ctrl+T — open / switch a tab. Rebindable through
    ///     <see cref="ChatKeyMap" /> like every other entry; the reducer turns it
    ///     into <see cref="TuiEffect.RequestOpenSession" /> for the host.
    /// </summary>
    OpenTab,

    /// <summary>
    ///     Alt+Left — move the focused tab one step left (#390, slice 3/3).
    ///     The keyboard equivalent of drag-reorder: a mouse-only reorder is not
    ///     acceptable on this surface (SSH, multiplexers), so the chord folds
    ///     through the existing <c>ReorderTab</c> transition and keeps focus.
    /// </summary>
    MoveTabLeft,

    /// <summary>Alt+Right — move the focused tab one step right (mirrors <see cref="MoveTabLeft" />).</summary>
    MoveTabRight,

    // ── tab reopen + unread model (#1173, opencode steal) ────────────────────
    // Same split as the #389 actions above: the keymap owns "which key", the
    // reducer owns "what it means", the host owns "switch the session".

    /// <summary>
    ///     Ctrl+Shift+T — restore the most recently closed tab at its original
    ///     position. Always available (even with one tab open): the stack is
    ///     about closed tabs, not the open count.
    /// </summary>
    ReopenTab,

    /// <summary>Alt+Shift+Down — focus the next tab with an unread signal (wraps).</summary>
    NextUnreadTab,

    /// <summary>Alt+Shift+Up — focus the previous tab with an unread signal (wraps).</summary>
    PreviousUnreadTab,

    /// <summary>Ctrl+1..Ctrl+9 — focus the Nth tab in tab order. Slot index comes from the key's Character.</summary>
    ActivateTabSlot
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
    /// <summary>A panel owns focus; the specific panel id is in <c>UiState.Ui.FocusedPanelId</c>.</summary>
    Panel
}

/// <summary>
///     Shared command vocabulary for the interactive chat. Lives in abstractions so
///     the pure reducer (autocomplete) and the effect host (slash dispatch) read from
///     one source of truth instead of a concrete host type.
/// </summary>
public static class ChatCommands
{
    /// <summary>
    ///     Slash commands offered by autocomplete and accepted as input.
    ///     Derived from <see cref="SlashCommandCatalog" /> (#462) — the same
    ///     registry the CLI dispatcher binds handlers to, so autocomplete
    ///     cannot offer a command the dispatcher does not implement.
    /// </summary>
    public static readonly ImmutableArray<string> Slash =
        [.. SlashCommandCatalog.Invocations];

    /// <summary>Words that quit the interactive loop when submitted as input.</summary>
    public static readonly ImmutableHashSet<string> ExitWords =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "exit", "quit", ":q");
}
