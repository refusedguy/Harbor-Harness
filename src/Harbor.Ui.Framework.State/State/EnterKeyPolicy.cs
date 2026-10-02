using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Single source of truth for the Enter key family (issue #359, TEA/SRP):
///     plain Enter submits, Shift+Enter / Alt+Enter insert a newline, Ctrl+Enter
///     is ignored. <see cref="ChatKeyMap"/> exposes the same decision as
///     bindings (<see cref="ChatAction.Submit"/> / <see cref="ChatAction.InsertNewline"/> /
///     <see cref="ChatAction.None"/>); <see cref="ChatAppReducer.Update"/> owns the
///     state transition; the CellForge <c>ComposerController</c> mirrors it as a
///     pure executor of store state so key behavior never diverges per renderer.
/// </summary>
/// <remarks>
///     Meta folds into Alt (terminal convention, cf. <c>KeyEventMapper</c>),
///     so Meta+Enter is a newline like Alt+Enter.
///     <para>
///     #33/T2 made this class an ADAPTER rather than the policy. The rules moved
///     down to <see cref="EnterPolicy"/>, the BCL-only twin, because the CellForge
///     <c>ComposerController</c> needs the decision and the engine cannot reference
///     State — asking for the decision was the whole reason the engine's last
///     <c>Harbor.Ui.Framework.State</c> edge existed. Moving the rules down rather
///     than duplicating them upward is what keeps #359's promise: there is still
///     exactly one place that decides, and both sides call it. The three cases map
///     1:1 and in the same precedence, so no behaviour changed.
///     </para>
/// </remarks>
public static class EnterKeyPolicy
{
    /// <summary>Resolve the Enter-family action from a State modifier set.</summary>
    /// <remarks>
    /// <see cref="KeyModifierSet" /> carries no Meta member, so the Meta case is
    /// unreachable here by construction — a caller that has seen Meta has already
    /// had it folded into Alt upstream (cf. <c>KeyEventMapper</c>).
    /// </remarks>
    public static ChatAction Resolve(KeyModifierSet mods) =>
        ToAction(EnterPolicy.Resolve(
            mods.HasFlag(KeyModifierSet.Ctrl),
            mods.HasFlag(KeyModifierSet.Shift),
            mods.HasFlag(KeyModifierSet.Alt),
            meta: false));

    /// <summary>Resolve the Enter-family action from raw modifier flags.</summary>
    public static ChatAction Resolve(bool ctrl, bool shift, bool alt, bool meta) =>
        ToAction(EnterPolicy.Resolve(ctrl, shift, alt, meta));

    /// <summary>
    ///     The vocabulary half of the adapter: <see cref="EnterDecision" /> in,
    ///     <see cref="ChatAction" /> out. Kept as an exhaustive switch rather than
    ///     three <c>if</c>s so that a fourth <see cref="EnterDecision" /> member —
    ///     added on the low side, where it is free — cannot be silently dropped
    ///     here instead of reaching the store.
    /// </summary>
    private static ChatAction ToAction(EnterDecision decision) => decision switch
    {
        EnterDecision.InsertNewline => ChatAction.InsertNewline,
        EnterDecision.Submit => ChatAction.Submit,
        _ => ChatAction.None,
    };
}