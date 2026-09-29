using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     One row of the <see cref="ChatRole" /> presentation table: the header
///     label, whether the body is markdown-rendered, and the colour slot the hue
///     is taken from.
/// </summary>
/// <param name="Label">Text shown in the <c>─ role ─</c> band.</param>
/// <param name="Markdown">True if the role's body is rendered as markdown.</param>
/// <param name="Slot">Semantic colour bucket for the role.</param>
public readonly record struct ChatRolePolicy(string Label, bool Markdown, ChatColorSlot Slot);

/// <summary>
///     The single, platform-neutral presentation policy for a
///     <see cref="ChatRole" /> — which header label to print, whether the body is
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
///         <b>Why a table and not a <c>switch</c>.</b> docs/PATTERNS.md §"Type
///         unions" requires a <c>switch</c> over a Harbor union to have every arm
///         named and no wildcard — and <c>ExhaustiveUnionSwitchRule</c> enforces
///         that by scanning for <c>_ =&gt;</c> / <c>default:</c>. A
///         <c>switch</c> <em>expression</em> cannot satisfy it: the compiler
///         rejects a discard-free one over an enum with <c>CS8524</c>
///         ("not exhaustive, involving an unnamed enum value") rather than
///         accepting it as a total match, so the only compiler-clean shapes are a
///         wildcard (forbidden) or a <c>switch</c> statement (which falls through
///         in silence). The table is the third way: one row per role, an explicit
///         loud guard in front of it, and a row count the reflection test pins to
///         <see cref="Enum.GetValues{TEnum}()" />.
///     </para>
///     <para>
///         <b>The guard is loud, by design.</b> An unhandled role — a new
///         <see cref="ChatRole" /> member whose row was never added, or a value
///         outside the declared range — throws with the fix in the message
///         instead of rendering as <c>"msg"</c>. <c>ChatRole</c> is never
///         persisted (it is produced in-memory by
///         <c>SessionFactory.MessageToChatLine</c>), so an unhandled value is
///         always a missing row, never corrupt stored data.
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
    /// <summary>
    ///     The policy table: one row per <see cref="ChatRole" />, in declaration
    ///     order (<c>User</c> = 0 … <c>Error</c> = 6).
    /// </summary>
    /// <remarks>
    ///     The length is the union's arity, and
    ///     <c>ChatRolePresentationTests</c> asserts it against
    ///     <see cref="Enum.GetValues{TEnum}()" />, so a new member fails CI here
    ///     and throws at runtime. A row is never defaulted: a gap is null and
    ///     the guard below treats it exactly like a missing row.
    /// </remarks>
    private static readonly ChatRolePolicy?[] Policies =
    [
        new("you", true, ChatColorSlot.User),           // ChatRole.User
        new("assistant", true, ChatColorSlot.Assistant), // ChatRole.Assistant
        new("thinking", false, ChatColorSlot.Muted),    // ChatRole.Thinking
        new("tool", false, ChatColorSlot.Tool),         // ChatRole.Tool
        new("result", false, ChatColorSlot.Muted),      // ChatRole.ToolResult
        new("system", true, ChatColorSlot.Muted),       // ChatRole.System
        new("error", false, ChatColorSlot.Danger)       // ChatRole.Error
    ];

    /// <summary>
    ///     The policy for one role. Throws if the role has no row, rather than
    ///     answering with a default — see the remarks.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="role" /> has no row in <see cref="Policies" />: a
    ///     <see cref="ChatRole" /> member was added without updating the table.
    /// </exception>
    public static ChatRolePolicy Describe(ChatRole role)
    {
        int index = (int)role;
        if ((uint)index >= (uint)Policies.Length || Policies[index] is not ChatRolePolicy policy)
            throw Unhandled(role);

        return policy;
    }

    /// <summary>Header label shown in the <c>─ role ─</c> band.</summary>
    public static string Label(ChatRole role) => Describe(role).Label;

    /// <summary>True if the role's body is rendered with markdown spans.</summary>
    public static bool UsesMarkdown(ChatRole role) => Describe(role).Markdown;

    /// <summary>Semantic colour bucket the role's hue is taken from.</summary>
    public static ChatColorSlot Slot(ChatRole role) => Describe(role).Slot;

    /// <summary>
    ///     The one error this table raises. Shared with the backend palette
    ///     tables so all of them name the same fix.
    /// </summary>
    public static ArgumentOutOfRangeException Unhandled(ChatRole role) => new(
        nameof(role),
        role,
        "No ChatRole presentation row — add the role to ChatRolePresentation.Policies "
        + "and to ChatRolePresentationTests.Pinned, then give it a ChatColorSlot.");

    /// <summary>
    ///     The same error for a <see cref="ChatColorSlot" /> no backend palette
    ///     covers. Slots only originate from <see cref="Slot(ChatRole)" />, so
    ///     reaching this is a missing palette arm, not bad data.
    /// </summary>
    public static ArgumentOutOfRangeException UnhandledSlot(ChatColorSlot slot) => new(
        nameof(slot),
        slot,
        "No palette entry for this ChatColorSlot — add it to every backend's slot→colour table.");
}
