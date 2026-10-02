namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
///     Renderer-side Enter-family decision (epic #33 T2). BCL-only mirror of the
///     three <c>ChatAction</c> members <c>EnterKeyPolicy</c> resolves to: same
///     three cases, same order, same precedence — the State-side
///     <c>EnterKeyPolicy</c> maps this onto <c>ChatAction</c> explicitly, so the
///     two must stay 1:1.
/// </summary>
/// <remarks>
///     This type exists because the CellForge <c>ComposerController</c> must decide
///     what Enter does before it knows what Enter <i>means</i>. It used to ask
///     <c>EnterKeyPolicy</c>, which made
///     <c>Harbor.Ui.Framework.State</c> a dependency of the terminal engine —
///     the last one, and the reason #435 could not delete the edge. The policy did
///     not move: it stayed single-sourced, the way #359 wanted, because the State
///     side now delegates to this instead of the other way round. Only the
///     <c>ChatAction</c> vocabulary moved up, which is the direction the layering
///     matrix allows.
/// </remarks>
public enum EnterDecision : byte
{
    /// <summary>Ignored — Ctrl+Enter, per #359.</summary>
    None = 0,

    /// <summary>Insert a newline — Shift/Alt/Meta+Enter.</summary>
    InsertNewline = 1,

    /// <summary>Submit the draft — unmodified Enter.</summary>
    Submit = 2,
}

/// <summary>
///     The Enter-key decision, free of any State reference. Plain Enter submits,
///     Shift+Enter / Alt+Enter insert a newline, Ctrl+Enter is ignored.
/// </summary>
/// <remarks>
///     Meta folds into Alt (terminal convention, cf. <c>KeyEventMapper</c>), so
///     Meta+Enter is a newline like Alt+Enter.
/// </remarks>
public static class EnterPolicy
{
    /// <summary>Resolve the Enter-family decision from raw modifier flags.</summary>
    public static EnterDecision Resolve(bool ctrl, bool shift, bool alt, bool meta)
    {
        if (ctrl)
            return EnterDecision.None;
        if (shift || alt || meta)
            return EnterDecision.InsertNewline;
        return EnterDecision.Submit;
    }
}