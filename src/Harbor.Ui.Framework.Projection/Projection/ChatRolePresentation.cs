using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     The single, platform-neutral presentation policy for a
///     <see cref="ChatRole" />: which header label to print, whether the body is
///     markdown-rendered, and which <see cref="ChatColorSlot" /> the role's hue
///     is taken from.
/// </summary>
/// <remarks>
///     <para>
///         This type replaced four hand-maintained copies of the same table
///         (#556) — <c>RazorColorMapper</c>, <c>TerminaColorMapper</c>,
///         <c>TerminalGuiColorMapper</c> and the rule inlined in SpectreTui's
///         <c>ChatMessageFormatter</c>. Three of the four were byte-identical and
///         all four ended in a wildcard arm, so adding a role relabelled it
///         <c>"msg"</c> in every renderer with no warning and no failing test.
///     </para>
///     <para>
///         <b>No wildcard arm, on purpose.</b> <see cref="Describe" /> is a
///         switch expression over <see cref="ChatRole" /> with no discard arm, so
///         the compiler reports <c>CS8509</c> the moment a new role is added to
///         the enum. The next role is a build break, not a silent relabel.
///     </para>
///     <para>
///         <b>What is shared, what is not.</b> Label, markdown rule and colour
///         slot are shared by all four chat backends, so they cannot drift. The
///         concrete hue is a per-backend palette decision and is deliberately
///         <em>not</em> shared: Termina has 24-bit RGB, Terminal.Gui has its own
///         ANSI names (<c>Bright*</c>), Spectre markup has neither. What is
///         invariant now — and asserted — is that two roles sharing a slot get
///         the same hue <em>within</em> a backend, and that the slot itself is
///         the same across backends. Header emphasis (bold / italic) stays a
///         per-backend capability: only SpectreTui paints a decorated speaker
///         band. Do not reintroduce a "same hue" claim — the three mappers that
///         carried it were false, which is what #556 is about.
///     </para>
/// </remarks>
public static class ChatRolePresentation
{
    /// <summary>Header label shown in the <c>─ role ─</c> band.</summary>
    public static string Label(ChatRole role) => Describe(role).Label;

    /// <summary>True if the role's body is rendered with markdown spans.</summary>
    public static bool UsesMarkdown(ChatRole role) => Describe(role).Markdown;

    /// <summary>Semantic colour bucket the role's hue is taken from.</summary>
    public static ChatColorSlot Slot(ChatRole role) => Describe(role).Slot;

    /// <summary>
    ///     The whole policy for one role in a single tuple, so label, markdown
    ///     rule and slot can never be decided in different places.
    /// </summary>
    public static (string Label, bool Markdown, ChatColorSlot Slot) Describe(ChatRole role) => role switch
    {
        ChatRole.User => ("you", true, ChatColorSlot.User),
        ChatRole.Assistant => ("assistant", true, ChatColorSlot.Assistant),
        ChatRole.Thinking => ("thinking", false, ChatColorSlot.Muted),
        ChatRole.Tool => ("tool", false, ChatColorSlot.Tool),
        ChatRole.ToolResult => ("result", false, ChatColorSlot.Muted),
        ChatRole.System => ("system", true, ChatColorSlot.Muted),
        ChatRole.Error => ("error", false, ChatColorSlot.Danger)
    };
}
