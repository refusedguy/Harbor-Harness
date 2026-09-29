using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Semantic colour bucket for a <see cref="ChatRole" />.
/// </summary>
/// <remarks>
///     <para>
///         A slot names an <em>intent</em>, never a colour: every chat backend
///         maps it onto its own palette (Spectre <c>Color</c>, Termina 24-bit
///         RGB, Terminal.Gui <c>Color</c>, Spectre markup token). Two roles
///         that share a slot are therefore painted the same hue by a given
///         backend — that is the invariant the slot buys and the one
///         <c>ChatRolePresentationTests</c> pins.
///     </para>
///     <para>
///         Deliberately distinct from <see cref="UiSpanStyle" />: a span style
///         is a semantic mark on one projected span (status bar, tool card, and
///         it doubles as a general-purpose vocabulary with <c>Dim</c> /
///         <c>Accent</c> / <c>Success</c>), while a slot is a chat-band palette
///         bucket. Mixing them would drag status-bar vocabulary into the
///         transcript band.
///     </para>
///     <para>
///         Adding a member is a compile error in every backend palette table —
///         they are switch expressions without a wildcard arm. See
///         <see cref="ChatRolePresentation" /> for the role→slot table.
///     </para>
/// </remarks>
public enum ChatColorSlot : byte
{
    /// <summary>The user's own input — green in every backend.</summary>
    User,

    /// <summary>The assistant's reply — the default body hue in every backend.</summary>
    Assistant,

    /// <summary>De-emphasised chatter: thinking, tool results, system notices.</summary>
    Muted,

    /// <summary>Tool invocation lines — blue in every backend.</summary>
    Tool,

    /// <summary>Errors — red in every backend.</summary>
    Danger
}
