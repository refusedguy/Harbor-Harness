namespace Harbor.Ui.Framework.State;

/// <summary>
///     Single source of truth for the Enter key family (issue #359, TEA/SRP):
///     plain Enter submits, Shift+Enter / Alt+Enter insert a newline, Ctrl+Enter
///     is ignored. <see cref="ChatKeyMap"/> exposes the same decision as
///     bindings (<see cref="ChatAction.Submit"/> / <see cref="ChatAction.InsertNewline"/> /
///     <see cref="ChatAction.None"/>); <see cref="UiReducer.Update"/> owns the
///     state transition; the CellForge <c>ComposerController</c> mirrors it as a
///     pure executor of store state so key behavior never diverges per renderer.
/// </summary>
/// <remarks>
///     Meta folds into Alt (terminal convention, cf. <c>KeyEventMapper</c>),
///     so Meta+Enter is a newline like Alt+Enter.
/// </remarks>
public static class EnterKeyPolicy
{
    /// <summary>Resolve the Enter-family action from a State modifier set.</summary>
    public static ChatAction Resolve(KeyModifierSet mods)
    {
        if (mods.HasFlag(KeyModifierSet.Ctrl))
            return ChatAction.None;
        if (mods.HasFlag(KeyModifierSet.Shift) || mods.HasFlag(KeyModifierSet.Alt))
            return ChatAction.InsertNewline;
        return ChatAction.Submit;
    }

    /// <summary>Resolve the Enter-family action from raw modifier flags.</summary>
    public static ChatAction Resolve(bool ctrl, bool shift, bool alt, bool meta)
    {
        if (ctrl)
            return ChatAction.None;
        if (shift || alt || meta)
            return ChatAction.InsertNewline;
        return ChatAction.Submit;
    }
}
