namespace Harbor.Ui.Framework.State;
/// <summary>
///     Central registry of key bindings + human-readable labels for the chat UI.
///     Every shell translates its native key into <see cref="UiKey" /> and resolves
///     it here, so this table is the single source of truth for "what key does
///     what" across all renderers. Shells must not add their own key→action
///     branches — put the binding here and handle the action in the reducer.
/// </summary>
public sealed class ChatKeyMap
{

    private readonly Dictionary<ChatAction, Entry> _byAction;

    private readonly Entry[] _entries =
    [
        new(ChatAction.Quit, "quit", new Binding(UiKeyCode.Escape)),
        // Ctrl+C — reported by most frameworks as a character, not a key code.
        new(ChatAction.Abort, "abort",
            new Binding(UiKeyCode.Escape),
            new Binding(UiKeyCode.Char, KeyModifierSet.Ctrl, 'c')),
        new(ChatAction.Submit, "send", new Binding(UiKeyCode.Enter)),
        new(ChatAction.ToggleFocus, "focus", new Binding(UiKeyCode.F2)),
        new(ChatAction.ScrollUpLine, "up", new Binding(UiKeyCode.Up)),
        new(ChatAction.ScrollDownLine, "down", new Binding(UiKeyCode.Down)),
        new(ChatAction.ScrollUpPage, "page up", new Binding(UiKeyCode.PageUp)),
        new(ChatAction.ScrollDownPage, "page down", new Binding(UiKeyCode.PageDown)),
        new(ChatAction.ScrollTop, "top", new Binding(UiKeyCode.Home)),
        new(ChatAction.ScrollBottom, "bottom", new Binding(UiKeyCode.End)),
        new(ChatAction.InputHistoryPrev, "prev input", new Binding(UiKeyCode.Up, KeyModifierSet.Alt)),
        new(ChatAction.InputHistoryNext, "next input", new Binding(UiKeyCode.Down, KeyModifierSet.Alt)),
        new(ChatAction.Autocomplete, "complete", new Binding(UiKeyCode.Tab)),
        new(ChatAction.Backspace, "backspace", new Binding(UiKeyCode.Backspace)),
        // Ctrl+L — reported by most frameworks as a character, not a key code.
        new(ChatAction.Clear, "clear", new Binding(UiKeyCode.Char, KeyModifierSet.Ctrl, 'l')),
        // '?' — toggle help panel. Listed before the coarse Alt+char slot entry
        // below so Alt+'?' keeps resolving here, as the old shell overrides did.
        new(ChatAction.HelpPanel, "help", new Binding(UiKeyCode.Char, KeyModifierSet.None, '?')),

        // ── panel hotkeys ────────────────────────────────────────────────
        // Alt+1..Alt+9 — toggle the Nth registered panel. Slot comes from the key's Character.
        new(ChatAction.TogglePanelSlot, "panel 1", new Binding(UiKeyCode.Char, KeyModifierSet.Alt)),
        // Ctrl+Tab — cycle focus between visible panels and chat.
        new(ChatAction.CyclePanelFocus, "cycle panel", new Binding(UiKeyCode.Tab, KeyModifierSet.Ctrl)),
        // Ctrl+Up / Ctrl+Right — grow focused panel.
        new(ChatAction.ResizePanelGrow, "grow panel", new Binding(UiKeyCode.Up, KeyModifierSet.Ctrl)),
        // Ctrl+Down / Ctrl+Left — shrink focused panel.
        new(ChatAction.ResizePanelShrink, "shrink panel", new Binding(UiKeyCode.Down, KeyModifierSet.Ctrl)),
        // F12 — toggle the in-TUI diagnostics / logs panel.
        new(ChatAction.ToggleLogsPanel, "logs", new Binding(UiKeyCode.F12)),
        // Ctrl+J — open the worktree jump palette. Most frameworks report it as
        // 'j' + Ctrl, but some terminals send a bare LF (0x0A) with no Ctrl flag
        // instead — both resolve here so no shell needs its own branch.
        new(ChatAction.JumpPalette, "jump worktree",
            new Binding(UiKeyCode.Char, KeyModifierSet.Ctrl, 'j'),
            new Binding(UiKeyCode.Char, KeyModifierSet.None, '\n'))
    ];

    public ChatKeyMap()
    {
        _byAction = _entries.ToDictionary(e => e.Action);
    }

    /// <summary>All documented actions (used to render footer/help).</summary>
    public IReadOnlyList<Entry> All => _entries;

    /// <summary>Resolve a key press to an action (first matching entry wins).</summary>
    /// <remarks>
    ///     Entry order defines priority: explicit bindings are matched before the
    ///     implicit printable-character rule, so a key like 'q' with no binding still
    ///     falls through to <see cref="ChatAction.Char" /> and reaches the input box.
    /// </remarks>
    public ChatAction Resolve(UiKey key)
    {
        var match = _entries.FirstOrDefault(e => e.Bindings.Any(b => b.Matches(key)));
        if (match != null)
            return match.Action;

        // Any printable character (no special binding) is an input character.
        if (key.Code == UiKeyCode.Char && key.Character is not null)
            return ChatAction.Char;

        return ChatAction.None;
    }

    /// <summary>Get the documented entry for an action.</summary>
    public Entry Get(ChatAction action) => _byAction[action];

    /// <summary>Match spec: a key code, optionally gated by required modifiers and/or an exact character.</summary>
    /// <remarks>
    ///     A <see langword="null" /> character matches any character (e.g. the
    ///     Alt+1..Alt+9 panel slots); a set character restricts the binding to that
    ///     exact char (Ctrl+L clear, '?' help, Ctrl+J / LF jump palette). Character
    ///     bindings exist because most frameworks report Ctrl+letter combos as
    ///     characters rather than key codes — resolving them here keeps every
    ///     shell free of key→action branches.
    /// </remarks>
    public readonly record struct Binding(UiKeyCode Code, KeyModifierSet Mods = KeyModifierSet.None, char? Character = null)
    {
        public bool Matches(UiKey key)
            => key.Code == Code && key.Mods.HasFlag(Mods) && (Character is null || key.Character == Character);
    }

    /// <summary>One documented action: its label and the key bindings that trigger it.</summary>
    public sealed record Entry(ChatAction Action, string Label, params Binding[] Bindings);
}
