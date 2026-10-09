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
        // Shift+Enter / Alt+Enter — newline, not submit (kitty disambiguate
        // flags; see EnterKeyPolicy). Ctrl+Enter stays unbound → None
        // (ignored), matching the composer. Subset semantics: Shift also
        // matches Shift+Alt, Alt also matches Alt+Shift; Ctrl combos are
        // dropped by the reducer guard.
        new(ChatAction.InsertNewline, "newline",
            new Binding(UiKeyCode.Enter, KeyModifierSet.Shift),
            new Binding(UiKeyCode.Enter, KeyModifierSet.Alt)),
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

        // ── tab strip (#389) ─────────────────────────────────────────────
        // Ctrl+Tab / Ctrl+Shift+Tab — next / previous open tab. Listed BEFORE
        // CyclePanelFocus (which keeps the same key) and gated on a live
        // multi-tab strip: while two or more tabs are open the tab strip owns
        // Ctrl+Tab, otherwise the guard fails and the panel binding below wins
        // exactly as it did before tabs existed. Resolve(key) without a state
        // snapshot passes null, so the guard can never fire and the legacy
        // panel mapping is preserved for every existing caller.
        //
        // The two entries need separate guards because Binding's subset
        // semantics make the bare-Ctrl entry match Ctrl+Shift+Tab too: without
        // the Shift check the "next" entry would swallow "previous".
        new(ChatAction.NextTab, "next tab",
            new Binding(UiKeyCode.Tab, KeyModifierSet.Ctrl)) { Guard = TabStripOpenPlain },
        new(ChatAction.PreviousTab, "prev tab",
            new Binding(UiKeyCode.Tab, KeyModifierSet.Ctrl)) { Guard = TabStripOpenShifted },
        // Ctrl+W — close the focused tab. Unconditional: with one tab open this
        // is still a meaningful (and reversible-by-reopen) action, and it must
        // never fall through to the quit binding.
        new(ChatAction.CloseTab, "close tab", new Binding(UiKeyCode.Char, KeyModifierSet.Ctrl, 'w')),
        // Ctrl+T — open / switch tab. Rebindable through this table like every
        // other entry (the slice-3 gesture layer reuses the same action).
        new(ChatAction.OpenTab, "open tab", new Binding(UiKeyCode.Char, KeyModifierSet.Ctrl, 't')),
        // Alt+Left / Alt+Right — move the focused tab (#390, slice 3/3). The
        // Left/Right codes are unclaimed (resize owns Ctrl+Up/Down/Left/Right,
        // panel slots own Alt+char), so no ordering hazard. Guarded on a live
        // multi-tab strip like Next/PreviousTab: with fewer than two tabs the
        // chord falls through to None instead of no-op dispatching.
        new(ChatAction.MoveTabLeft, "move tab left",
            new Binding(UiKeyCode.Left, KeyModifierSet.Alt)) { Guard = TabStripGuard },
        new(ChatAction.MoveTabRight, "move tab right",
            new Binding(UiKeyCode.Right, KeyModifierSet.Alt)) { Guard = TabStripGuard },

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

    /// <summary>
    ///     Tab-strip guard for the plain Ctrl+Tab ("next tab") entry: a snapshot
    ///     must be supplied and at least two tabs must be open, otherwise the key
    ///     belongs to whatever binding comes next in table order.
    /// </summary>
    private static bool TabStripOpenPlain(UiKey key, UiState? state) =>
        !key.Mods.HasFlag(KeyModifierSet.Shift) && TabStripOpen(state);

    /// <summary>
    ///     Same guard for Ctrl+Shift+Tab ("previous tab"). Kept separate from
    ///     <see cref="TabStripOpenPlain" /> because <see cref="Binding" /> uses
    ///     subset matching: a bare-Ctrl binding also matches Ctrl+Shift+Tab, and
    ///     first-match-wins would then route "previous" to the "next" action.
    /// </summary>
    private static bool TabStripOpenShifted(UiKey key, UiState? state) =>
        key.Mods.HasFlag(KeyModifierSet.Shift) && TabStripOpen(state);

    /// <summary>
    ///     True when a state snapshot was supplied and it has a real tab strip
    ///     to act on. A <see langword="null" /> snapshot (the state-less
    ///     <see cref="Resolve(UiKey)" /> overload) never opens a tab, which keeps
    ///     the pre-#389 Ctrl+Tab → <see cref="ChatAction.CyclePanelFocus" />
    ///     mapping byte-for-byte intact for every caller that has no store.
    /// </summary>
    private static bool TabStripOpen(UiState? state) =>
        state is not null && state.Chat.TabStrip.Tabs.Length >= 2;

    /// <summary>
    ///     Context guard for the move-tab pair (#390): same strip rule as the
    ///     cycle pair, but the key is irrelevant — Alt+Left/Right are unclaimed,
    ///     so there is no chord to disambiguate, only a strip to require.
    /// </summary>
    private static bool TabStripGuard(UiKey _, UiState? state) => TabStripOpen(state);

    /// <summary>
    ///     Resolve a key press to an action (first matching entry wins), without
    ///     a state snapshot. Context-guarded entries can never fire here — see
    ///     <see cref="Resolve(UiKey, UiState?)" />.
    /// </summary>
    /// <remarks>
    ///     Entry order defines priority: explicit bindings are matched before the
    ///     implicit printable-character rule, so a key like 'q' with no binding still
    ///     falls through to <see cref="ChatAction.Char" /> and reaches the input box.
    /// </remarks>
    public ChatAction Resolve(UiKey key) => Resolve(key, state: null);

    /// <summary>
    ///     Resolve a key press against the current <see cref="UiState" /> so
    ///     context-sensitive entries (the tab-strip Ctrl+Tab pair, which shares
    ///     its key with <see cref="ChatAction.CyclePanelFocus" />) can win only
    ///     while the surface they act on is actually on screen.
    /// </summary>
    /// <remarks>
    ///     The state is a pure input to resolution — it is read, never stored,
    ///     so the keymap stays a singleton-safe value object. A
    ///     <see langword="null" /> snapshot disables every guard, degrading
    ///     exactly to <see cref="Resolve(UiKey)" />.
    /// </remarks>
    public ChatAction Resolve(UiKey key, UiState? state)
    {
        var match = _entries.FirstOrDefault(e => Applies(e, key, state));
        if (match != null)
            return match.Action;

        // Any printable character (no special binding) is an input character.
        if (key.Code == UiKeyCode.Char && key.Character is not null)
            return ChatAction.Char;

        return ChatAction.None;
    }

    /// <summary>Get the documented entry for an action.</summary>
    public Entry Get(ChatAction action) => _byAction[action];

    /// <summary>
    ///     Whether <paramref name="entry" /> claims <paramref name="key" />:
    ///     one of its bindings matches AND its context guard (if any) passes for
    ///     this snapshot. Guards run only after a binding hit, so they cost
    ///     nothing on the overwhelmingly common miss path.
    /// </summary>
    private static bool Applies(Entry entry, UiKey key, UiState? state) =>
        entry.Bindings.Any(b => b.Matches(key)) && (entry.Guard is null || entry.Guard(key, state));

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
        {
            if (key.Code != Code)
                return false;
            if (Character is not null && key.Character != Character)
                return false;
            // Bare key-code bindings (Mods.None) match only with no modifiers
            // held: otherwise plain Tab would shadow Ctrl+Tab (HasFlag(None)
            // is always true) and entry order would decide. Specified
            // modifiers keep subset semantics (Ctrl+Shift+Tab still cycles).
            // Character bindings keep subset semantics: '?' resolves with Alt
            // held (as the old shell overrides did), exact letters pin theirs.
            if (Code != UiKeyCode.Char && Mods == KeyModifierSet.None)
                return key.Mods == KeyModifierSet.None;
            return key.Mods.HasFlag(Mods);
        }
    }

    /// <summary>
    ///     One documented action: its label, the key bindings that trigger it,
    ///     and an optional context guard.
    /// </summary>
    /// <param name="Action">The action this entry resolves to.</param>
    /// <param name="Label">Human-readable label (help overlay, keymap footer).</param>
    /// <param name="Bindings">Key specs; the first that matches claims the press.</param>
    public sealed record Entry(
        ChatAction Action,
        string Label,
        params Binding[] Bindings)
    {
        /// <summary>
        ///     Optional context predicate, run after a binding hit. Needed because
        ///     two features can legitimately share a chord: <c>Ctrl+Tab</c> cycles
        ///     panels normally and cycles tabs while a tab strip is open (#389).
        ///     A <see langword="null" /> guard is unconditional.
        /// </summary>
        public Func<UiKey, UiState?, bool>? Guard { get; init; }
    }
}
