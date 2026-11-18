namespace Harbor.Ui.Framework.State;

/// <summary>
///     The single routing table for <see cref="ChatAction"/>. The chat pass
///     (<see cref="ChatAppReducer"/>) and the generic pass (<see cref="AppReducer"/>)
///     both look their action up here, so a key can no longer be claimed by two
///     independent switches that drift apart (#2622). This table is key-action
///     routing only: markup <c>AppMsg</c> dispatch stays in the typed
///     <c>MarkupDispatch</c> table added by #2621.
/// </summary>
/// <remarks>
///     The two passes keep their existing order. A chat-owned entry is claimed
///     by <see cref="ChatAppReducer"/> first and never reaches the generic pass;
///     a generic-owned entry is invisible to the chat pass. A chat fold may
///     still return <see langword="null"/> (submit and clear while busy), which
///     hands the message back to the generic pass exactly as before.
/// </remarks>
internal static class KeyActionRegistry
{
    /// <summary>Which reducer pass owns the action.</summary>
    internal enum Domain
    {
        Chat,
        Generic
    }

    /// <summary>
    ///     One routing entry. <c>Fold</c> takes the current state,
    ///     the key message and the busy flag, and returns <see langword="null"/>
    ///     only when the owning pass declines the message.
    /// </summary>
    internal sealed record Entry(
        Domain Owner,
        Func<UiState, AppMsg.KeyInput, bool, ReduceResult?> Fold,
        bool AllowedWhileBusy = false);

    private static readonly IReadOnlyDictionary<ChatAction, Entry> Actions =
        new Dictionary<ChatAction, Entry>
        {
            // ── chat-owned (claimed in phase 1) ──────────────────────────
            // Quit is busy-sensitive: during a run Escape aborts instead.
            [ChatAction.Quit] = Chat(static (s, _, busy) => busy
                ? ChatAppReducer.TransitionAbort(s)
                : new ReduceResult(s, new TuiEffect.QuitApp())),
            [ChatAction.Abort] = Chat(static (s, _, _) => ChatAppReducer.TransitionAbort(s)),
            [ChatAction.Submit] = Chat(static (s, _, busy) =>
            {
                if (busy)
                    return null;
                (var input, string? submitted) = s.Ui.Input.Consume();
                var next = s.SetInput(input);
                return submitted is null
                    ? ReduceResult.NoOp(next)
                    : ChatAppReducer.ClassifySubmit(next, submitted);
            }),
            [ChatAction.Clear] = Chat(static (s, _, busy) =>
            {
                if (busy)
                    return null;
                return ReduceResult.NoOp(s.ClearTranscript());
            }),

            // Tab-strip keys are chrome: deliberately not gated on busy.
            [ChatAction.NextTab] = Chat(static (s, _, _) => ChatAppReducer.NextTabFromKey(s)),
            [ChatAction.PreviousTab] = Chat(static (s, _, _) => ChatAppReducer.PreviousTabFromKey(s)),
            [ChatAction.CloseTab] = Chat(static (s, _, _) => ChatAppReducer.CloseFocusedTab(s)),
            [ChatAction.OpenTab] = Chat(static (s, _, _) => ChatAppReducer.RequestOpenTab(s)),
            [ChatAction.ReopenTab] = Chat(static (s, _, _) => ChatAppReducer.ReopenTab(s)),
            [ChatAction.NextUnreadTab] = Chat(static (s, _, _) => ChatAppReducer.CycleUnreadTab(s, forward: true)),
            [ChatAction.PreviousUnreadTab] = Chat(static (s, _, _) => ChatAppReducer.CycleUnreadTab(s, forward: false)),
            [ChatAction.ActivateTabSlot] = Chat(static (s, k, _) => ChatAppReducer.ActivateSlotFromKey(s, k.Pressed)),
            [ChatAction.MoveTabLeft] = Chat(static (s, _, _) => ChatAppReducer.MoveFocusedTab(s, -1)),
            [ChatAction.MoveTabRight] = Chat(static (s, _, _) => ChatAppReducer.MoveFocusedTab(s, +1)),

            // ── generic (claimed in phase 2) ─────────────────────────────
            // Scroll and focus stay available while an operation is in flight.
            [ChatAction.ToggleFocus] = Generic(static (s, _, _) => ReduceResult.NoOp(s.SetFocus(
                s.Ui.Focus == FocusMode.Input ? FocusMode.Chat : FocusMode.Input)), allowedWhileBusy: true),
            [ChatAction.ScrollUpLine] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(s.SetScroll(s.Ui.ScrollOffset + 1)), allowedWhileBusy: true),
            [ChatAction.ScrollDownLine] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(s.SetScroll(s.Ui.ScrollOffset - 1)), allowedWhileBusy: true),
            [ChatAction.ScrollUpPage] = Generic(static (s, _, _) => ReduceResult.NoOp(
                s.SetScroll(s.Ui.ScrollOffset + Math.Max(1, s.Ui.ViewportLines - 2))), allowedWhileBusy: true),
            [ChatAction.ScrollDownPage] = Generic(static (s, _, _) => ReduceResult.NoOp(
                s.SetScroll(s.Ui.ScrollOffset - Math.Max(1, s.Ui.ViewportLines - 2))), allowedWhileBusy: true),
            [ChatAction.ScrollTop] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(s.SetScroll(int.MaxValue)), allowedWhileBusy: true),
            [ChatAction.ScrollBottom] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(s.SetScroll(0)), allowedWhileBusy: true),

            // Input editing is only accepted when the input box owns focus.
            [ChatAction.Backspace] = Generic(static (s, _, _) => Edit(s, new InputMsg.Backspace())),
            [ChatAction.InputHistoryPrev] = Generic(static (s, _, _) => Edit(s, new InputMsg.HistoryUp())),
            [ChatAction.InputHistoryNext] = Generic(static (s, _, _) => Edit(s, new InputMsg.HistoryDown())),
            [ChatAction.Autocomplete] = Generic(static (s, _, _) =>
                s.Ui.Focus == FocusMode.Input && s.Ui.Input.Text.StartsWith('/')
                    ? Edit(s, new InputMsg.Autocomplete(TuiEffectHost.KnownSlashCommands))
                    : ReduceResult.NoOp(s)),
            [ChatAction.Char] = Generic(static (s, k, _) =>
                s.Ui.Focus == FocusMode.Input && k.Pressed.Character is { } c
                    ? Edit(s, new InputMsg.Char(c))
                    : ReduceResult.NoOp(s)),
            [ChatAction.InsertNewline] = Generic(static (s, k, _) =>
                // Enter-family decision lives here (#359): Shift/Alt+Enter
                // appends '\n'; Ctrl combos are dropped because the composer
                // ignores Ctrl+Enter.
                s.Ui.Focus == FocusMode.Input && !k.Pressed.Has(KeyModifierSet.Ctrl)
                    ? Edit(s, new InputMsg.Char('\n'))
                    : ReduceResult.NoOp(s)),

            // Panel keys fold through the existing panel transitions.
            [ChatAction.TogglePanelSlot] = Generic(static (s, k, _) =>
                ReduceResult.NoOp(ToggleSlot(s, k))),
            [ChatAction.CyclePanelFocus] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(AppReducer.CycleFocus(s))),
            [ChatAction.ClosePanel] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(AppReducer.FocusPanel(s, null))),
            [ChatAction.ResizePanelGrow] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(ResizeFocused(s, +1))),
            [ChatAction.ResizePanelShrink] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(ResizeFocused(s, -1))),
            [ChatAction.HelpPanel] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(AppReducer.TogglePanel(s, "help"))),
            [ChatAction.WhichKey] = Generic(static (s, _, _) =>
                // Noop until a host registers a "whichkey" panel.
                ReduceResult.NoOp(AppReducer.TogglePanel(s, "whichkey"))),
            [ChatAction.ToggleLogsPanel] = Generic(static (s, _, _) =>
                ReduceResult.NoOp(AppReducer.TogglePanel(s, "logs"))),
            [ChatAction.JumpPalette] = Generic(static (s, _, _) =>
                // Noop until a host registers a "jump" panel/overlay.
                ReduceResult.NoOp(AppReducer.TogglePanel(s, "jump"))),
        };

    private static Entry Chat(Func<UiState, AppMsg.KeyInput, bool, ReduceResult?> fold) =>
        new(Domain.Chat, fold);

    private static Entry Generic(
        Func<UiState, AppMsg.KeyInput, bool, ReduceResult?> fold,
        bool allowedWhileBusy = false) => new(Domain.Generic, fold, allowedWhileBusy);

    /// <summary>Return the entry for <paramref name="action"/>, or null when unmapped.</summary>
    internal static Entry? Find(ChatAction action) =>
        Actions.TryGetValue(action, out Entry? entry) ? entry : null;

    /// <summary>Apply an input-model command, but only while the input box owns focus.</summary>
    private static ReduceResult Edit(UiState state, InputMsg command) =>
        state.Ui.Focus == FocusMode.Input
            ? ReduceResult.NoOp(state.SetInput(InputMsg.Update(state.Ui.Input, command)))
            : ReduceResult.NoOp(state);

    /// <summary>Alt+1..9: slot index from the pressed character.</summary>
    private static UiState ToggleSlot(UiState state, AppMsg.KeyInput k)
    {
        if (k.Pressed.Character is not { } c || c is < '1' or > '9')
            return state;
        int idx = c - '1';
        if (idx < 0 || idx >= state.Ui.RegisteredPanelIds.Length)
            return state;
        return AppReducer.TogglePanel(state, state.Ui.RegisteredPanelIds[idx]);
    }

    /// <summary>Grow/shrink the focused panel; noop when chat owns focus.</summary>
    private static UiState ResizeFocused(UiState state, int delta) =>
        state.Ui.FocusedPanelId is { } id ? AppReducer.ResizePanel(state, id, delta) : state;
}
