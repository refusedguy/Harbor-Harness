using System.Collections.Immutable;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Persisted tab-strip workspace (#390, slice 3/3): open-tab order plus
///     the focused tab, as plain session-id strings.
/// </summary>
/// <remarks>
///     <para>
///         Ids only, on purpose. Descriptors (<see cref="SessionTab" /> titles,
///         flags, panels) are rebuilt by the host from the session store at
///         restore time and arrive with the snapshot in
///         <see cref="ChatAppMsg.HydrateTabStrip" />, so a stale payload can
///         never resurrect a deleted session's metadata — ids with no
///         descriptor are dropped with a single transcript note.
///     </para>
///     <para>
///         The host persists this on tab-strip change (debounced, never per
///         frame) and rehydrates after first paint, so restoration never blocks
///         startup on a slow store. An empty or degenerate payload restores to
///         an empty strip without crashing; the host then opens the default
///         tab through the existing open path.
///     </para>
/// </remarks>
/// <param name="Order">
///     Open session ids, left to right. May name sessions that no longer exist
///     (dropped on restore) or repeat (first occurrence wins).
/// </param>
/// <param name="ActiveSessionId">
///     Focused session id, or <see langword="null" />. Unknown on restore →
///     the first restored tab.
/// </param>
public sealed record TabStripSnapshot(ImmutableArray<string> Order, string? ActiveSessionId)
{
    /// <summary>Capture the order + focus of <paramref name="strip" /> for the store.</summary>
    public static TabStripSnapshot FromStrip(TabStripState strip) => new(
        strip.Tabs.Select(t => t.SessionId.Value).ToImmutableArray(),
        strip.ActiveTabId?.Value);
}
