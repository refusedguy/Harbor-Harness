using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Tab-strip slice of the chat state (#388, slice 1/3 of #23): which
///     sessions are open right now, in which order, and which one is focused.
/// </summary>
/// <remarks>
///     <para>
///         Sits next to <see cref="ChatDomainState.Sessions" /> — the flat list of
///         every session the store knows about — and is deliberately separate
///         from it: <c>Sessions</c> is the picker content, <c>Tabs</c> is the
///         open set. Two sources of truth for "which session is active" would
///         drift, so this slice owns <see cref="ActiveTabId" /> only; the host
///         keeps <see cref="ChatDomainState.ActiveSessionId" /> in sync through
///         <see cref="ChatAppMsg.SyncSessions" /> once the activate effect has run.
///     </para>
///     <para>
///         Read-only queries live here; every transition is a pure function in
///         <see cref="ChatAppReducer" /> (TEA: state in, transitions in the reducer,
///         no mutation anywhere else).
///     </para>
/// </remarks>
public sealed record TabStripState
{
    /// <summary>
    ///     Open tabs in tab order, left to right. The array index IS the order
    ///     (see <see cref="SessionTab" />), so reorder is an array move and the
    ///     active tab survives it untouched.
    /// </summary>
    public ImmutableArray<SessionTab> Tabs { get; init; } = ImmutableArray<SessionTab>.Empty;

    /// <summary>Session id of the focused tab, or <see langword="null" /> when no tab is open.</summary>
    public SessionId? ActiveTabId { get; init; }

    public static readonly TabStripState Empty = new();

    /// <summary>
    ///     Position of <paramref name="sessionId" /> in tab order, or <c>-1</c>
    ///     when the session has no tab. Compares the id's string value
    ///     (<see cref="SessionId" /> is a reference-typed value object, so
    ///     <c>==</c> would compare references).
    /// </summary>
    public int IndexOf(SessionId sessionId)
    {
        var tabs = Tabs;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].SessionId.Value, sessionId.Value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>The tab for <paramref name="sessionId" />, or <see langword="null" /> when it has none.</summary>
    public SessionTab? Find(SessionId sessionId)
    {
        int index = IndexOf(sessionId);
        return index < 0 ? null : Tabs[index];
    }

    /// <summary>Whether <paramref name="sessionId" /> has an open tab.</summary>
    public bool Contains(SessionId sessionId) => IndexOf(sessionId) >= 0;
}
