using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     One open tab in the tab-strip model — state only, no behaviour (#388,
///     slice 1/3 of the #23 tab-strip). A tab is the view of one
///     <see cref="SessionId" />: which session is open, how it is labelled, and
///     which panels it owns while it is open.
/// </summary>
/// <remarks>
///     <para>
///         Pure state by construction: the host builds the descriptor and
///         <see cref="ChatAppReducer" /> folds it. Nothing here touches the
///         filesystem, the session store, or DI, and there is no provider-local
///         mirror — the renderer reads <see cref="TabStripState.Tabs" /> off the
///         snapshot (§FP-005/TEA).
///     </para>
///     <para>
///         <b>Order is the array index.</b> <see cref="TabStripState.Tabs" /> is
///         the single source of truth for tab order, so the descriptor carries no
///         <c>Order</c> field: a second copy would drift, and
///         <see cref="ChatAppReducer.ReorderTab" /> clamps a target index instead.
///     </para>
///     <para>
///         <see cref="IsPinned" /> is a flag for the renderer, not an ordering
///         rule — pin/unpin never moves a tab, because order changes stay
///         explicit (drag, move-to-index) and therefore reviewable.
///     </para>
/// </remarks>
/// <param name="SessionId">The session this tab shows. The tab identity — never duplicated.</param>
/// <param name="Title">Label shown in the tab (stored for the strip; not rendered here).</param>
/// <param name="WorkingDirectory">Session working directory, shown as the tab's subtitle.</param>
/// <param name="ShortStatus">One-or-two-word status (<c>idle</c>, <c>run</c>, <c>err</c>) for the tab glyph.</param>
/// <param name="IsDirty">Unread / unfinished work in the session — drives the dirty dot.</param>
/// <param name="IsPinned">Pinned tabs are exempt from the strip's close gestures. Flag only, no reordering.</param>
public sealed record SessionTab(
    SessionId SessionId,
    string Title = "session",
    string WorkingDirectory = "",
    string ShortStatus = "idle",
    bool IsDirty = false,
    bool IsPinned = false)
{
    /// <summary>
    ///     Ids of the panels this tab owns while it is open. Not a positional
    ///     parameter on purpose: a plain init property keeps the primary
    ///     constructor's six display fields readable, and a default
    ///     <see cref="ImmutableArray{T}" /> (never enumerated) can never sneak in
    ///     from a caller. See the ownership rule on <see cref="ChatAppReducer.CloseTab" />.
    /// </summary>
    public ImmutableArray<string> PanelIds { get; init; } = ImmutableArray<string>.Empty;
}
