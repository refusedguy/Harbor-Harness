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
///         <b>The wildcard arm throws, on purpose.</b> C# will not check enum
///         exhaustiveness for us here — a switch expression over
///         <see cref="ChatRole" /> without a discard arm is rejected with
///         <c>CS8524</c> ("not exhaustive, involving an unnamed enum value"), not
///         accepted as a total match. So the default arm is a throw rather than
///         a quiet answer: a new role that nobody wired up fails loudly with the
///         fix in the message, instead of rendering as <c>"msg"</c> in every
///         backend. <c>ChatRole</c> is never persisted (it is produced in-memory
///         by <c>SessionFactory.MessageToChatLine</c>), so an unhandled value
///         cannot come from stored data — it is always a missing table row.
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
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="role" /> has no row here. That means a
    ///     <see cref="ChatRole" /> member was added without updating this table —
    ///     loud by design, see the remarks.
    /// </exception>
    public static (string Label, bool Markdown, ChatColorSlot Slot) Describe(ChatRole role) => role switch
    {
        ChatRole.User => ("you", true, ChatColorSlot.User),
        ChatRole.Assistant => ("assistant", true, ChatColorSlot.Assistant),
        ChatRole.Thinking => ("thinking", false, ChatColorSlot.Muted),
        ChatRole.Tool => ("tool", false, ChatColorSlot.Tool),
        ChatRole.ToolResult => ("result", false, ChatColorSlot.Muted),
        ChatRole.System => ("system", true, ChatColorSlot.Muted),
        ChatRole.Error => ("error", false, ChatColorSlot.Danger),
        _ => throw Unhandled(role)
    };

    /// <summary>
    ///     The one error this table can raise. Shared with the span-style and
    ///     palette tables so all of them name the same fix.
    /// </summary>
    public static ArgumentOutOfRangeException Unhandled(ChatRole role) => new(
        "role",
        role,
        "No ChatRole presentation row — add the role to ChatRolePresentation.Describe "
        + "and to ChatRolePresentationTests.Pinned, then give it a ChatColorSlot.");

    /// <summary>
    ///     The same throw for a <see cref="ChatColorSlot" /> that no backend
    ///     palette covers. Slots only originate from
    ///     <see cref="Slot(ChatRole)" />, so reaching this is a missing palette
    ///     arm, not bad data.
    /// </summary>
    public static ArgumentOutOfRangeException UnhandledSlot(ChatColorSlot slot) => new(
        "slot",
        slot,
        "No palette entry for this ChatColorSlot — add it to every backend's slot→colour table.");
}
