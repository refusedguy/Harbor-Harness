namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
/// Keyboard modifiers, bits 1–4 of the kitty modifier encoding
/// (shift=1, ctrl=2, alt=4, meta=8). Kitty super/hyper/meta all collapse
/// into <see cref="Meta"/>; caps-lock and num-lock are ignored.
/// </summary>
[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Meta = 8,
}

/// <summary>
/// The one place the command-modifier set is named (#833).
/// </summary>
/// <remarks>
/// <para>
/// A key press is EITHER text or a command, and which one is decided here and
/// nowhere else. <see cref="Ctrl" />, <see cref="Alt" /> and <see cref="Meta" />
/// are command modifiers: they turn a gesture into a chord the host owns.
/// <see cref="Shift" /> is NOT — it is a case-shaper, and a real encoder
/// delivers a capital as the capital rune PLUS <see cref="Shift" />. So a gate
/// that refuses Shift refuses the letter with it.
/// </para>
/// <para>
/// <b>Why a predicate and not a widget property.</b> The question a widget
/// actually asks is "may a rune reach a buffer?", and the tree answers it as a
/// function of (widget, state, key) rather than of the type:
/// <c>QuestionFormView</c> is a text buffer on its custom row and command-only
/// on its option rows, decided by the cursor; <c>DialogOverlay</c> by
/// <c>_kind</c>; <c>VimComposerMode</c> by <c>NormalMode</c>, where its own
/// command gate at <c>:37</c> falls through to the composer for anything it
/// rejects. No boolean a type declares can carry that, which is why this is a
/// function the call site applies rather than an interface a widget implements
/// (the same shape as the <c>ITuiPlugin</c> seam #564 froze).
/// </para>
/// <para>
/// <b>Which predicate a site wants.</b> Refusing <see cref="Ctrl" />/
/// <see cref="Alt" />/<see cref="Meta" /> is
/// <see cref="AcceptsTypedChar" />, and admitting <see cref="Shift" /> as well
/// is the same call — the two are one predicate, and a site that needs Shift
/// refused is asking for something this type deliberately does not offer.
/// That is not the same as "no modifiers at all": a widget binding a shifted
/// rune of its own (<c>DiffViewerOverlay</c> binds <c>G</c>,
/// <c>ImageViewerOverlay</c> binds <c>_</c>) holds no text buffer and still
/// admits Shift, because Shift means something AT THAT SITE.
/// </para>
/// </remarks>
public static class KeyModifierGate
{
    /// <summary>The command modifiers, as one mask. Shift is deliberately absent.</summary>
    public const KeyModifiers CommandMask = KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta;

    /// <summary>
    /// Whether NO modifier at all is held — the OTHER half of the rule, for a
    /// widget whose runes are commands rather than text.
    /// </summary>
    /// <remarks>
    /// This is strictly narrower than <c>!IsCommandModifier()</c>, and the
    /// difference is the whole taxonomy: a command-only widget (h/j/k/l, or a
    /// y/n/a vote) refuses Shift too, because Shift changes nothing a user could
    /// want there. Use this one when the rune is a command; use
    /// <see cref="AcceptsTypedChar" /> when a rune can be text.
    /// <para>
    /// It is here so the choice is named at the call site rather than written as
    /// a bare <c>!= KeyModifiers.None</c> that a reader has to interpret — the
    /// four-spelling problem #833 is about.
    /// </para>
    /// </remarks>
    /// <param name="modifiers">The modifiers reported with the key press.</param>
    public static bool IsUnmodified(this KeyModifiers modifiers) => modifiers == KeyModifiers.None;

    /// <summary>
    /// Whether this key press may be treated as TEXT rather than as a chord.
    /// True when no command modifier is held — Shift alone still passes, because
    /// Shift is a case-shaper and a capital arrives as the rune plus Shift.
    /// </summary>
    /// <param name="modifiers">The modifiers reported with the key press.</param>
    public static bool AcceptsTypedChar(this KeyModifiers modifiers) =>
        (modifiers & CommandMask) == 0;

    /// <summary>
    /// Whether a command modifier is held, i.e. the press is a chord the host
    /// owns and this widget must not claim. The negation of
    /// <see cref="AcceptsTypedChar" />, named for the case where that reads
    /// better at the call site.
    /// </summary>
    /// <param name="modifiers">The modifiers reported with the key press.</param>
    public static bool IsCommandModifier(this KeyModifiers modifiers) =>
        (modifiers & CommandMask) != 0;
}

/// <summary>
/// The <see cref="ConsoleModifiers" /> half of the command-modifier rule
/// (#833), for the legacy <see cref="ConsoleKeyInfo" /> path.
/// </summary>
/// <remarks>
/// The mask is <see cref="System.ConsoleModifiers.Control" /> |
/// <see cref="System.ConsoleModifiers.Alt" /> — a STRICT SUBSET of the kitty
/// vocabulary's, because <see cref="ConsoleKeyInfo" /> has no Meta slot. That is
/// why this cannot be a conversion of the <see cref="KeyModifiers" /> mask: the
/// two overloads answer different questions on the same gesture, and pretending
/// otherwise is how the legacy path drifted from the kitty one in the first
/// place (#775). Shift is still not a command modifier — <c>?</c> is a shifted
/// rune and must stay dismissable.
/// </remarks>
public static class ConsoleModifierGate
{
    /// <summary>The command modifiers, as one mask. Shift is deliberately absent.</summary>
    public const ConsoleModifiers CommandMask = ConsoleModifiers.Control | ConsoleModifiers.Alt;

    /// <summary>
    /// Whether NO modifier at all is held — the other half of the rule, for a
    /// widget whose runes are commands rather than text. Strictly narrower than
    /// <c>!IsCommandModifier()</c>, which still admits Shift.
    /// </summary>
    /// <param name="modifiers">The modifiers reported with the key press.</param>
    public static bool IsUnmodified(this ConsoleModifiers modifiers) => modifiers == ConsoleModifiers.None;

    /// <summary>
    /// Whether this key press may be treated as TEXT rather than as a chord.
    /// True when no command modifier is held — Shift alone still passes.
    /// </summary>
    /// <param name="modifiers">The modifiers reported with the key press.</param>
    public static bool AcceptsTypedChar(this ConsoleModifiers modifiers) =>
        (modifiers & CommandMask) == 0;

    /// <summary>
    /// Whether a command modifier is held, i.e. the press is a chord the host
    /// owns and this widget must not claim.
    /// </summary>
    /// <param name="modifiers">The modifiers reported with the key press.</param>
    public static bool IsCommandModifier(this ConsoleModifiers modifiers) =>
        (modifiers & CommandMask) != 0;
}
