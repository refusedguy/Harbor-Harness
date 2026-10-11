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

    /// <summary>
    ///     Show the strip even with fewer than two tabs open (#389). The default
    ///     is <see langword="false" />: a lone tab is dead chrome, and every
    ///     golden in the repo was captured without it. Hosts read this from user
    ///     config ("always show the tab bar") and seed it into the initial
    ///     snapshot — it is a display preference, not a transition, so it
    ///     carries no <c>UiMsg</c>.
    /// </summary>
    public bool ForceShow { get; init; }

    /// <summary>
    ///     Whether a renderer should paint the strip for the given tab count:
    ///     two or more open tabs, or an explicit <see cref="ForceShow" />.
    ///     Single source of truth for the rule so the widget, the layout
    ///     attach/detach and the tests cannot disagree.
    /// </summary>
    public bool ShouldRender => ForceShow || Tabs.Length >= 2;

    public static readonly TabStripState Empty = new();

    /// <summary>
    ///     Reopen-stack depth (#1173): at most this many closed tabs are kept
    ///     for <c>Ctrl+Shift+T</c>. Same bound as opencode's
    ///     <c>CLOSED_SESSION_TAB_LIMIT</c> — a long-lived TUI must not
    ///     accumulate one entry per closed session forever.
    /// </summary>
    public const int ReopenLimit = 25;

    /// <summary>
    ///     Recently closed tabs, oldest first (#1173). Pushed by every close
    ///     transition, popped by the reopen transition, pruned when a session
    ///     is (re)opened through <c>OpenTab</c>. Session-local memory only —
    ///     deliberately not part of <see cref="TabStripSnapshot" />, which
    ///     persists the open order alone.
    /// </summary>
    public ImmutableArray<ClosedTab> ClosedStack { get; init; } = ImmutableArray<ClosedTab>.Empty;

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

    /// <summary>
    ///     The next tab carrying an unread signal after <paramref name="from" />
    ///     (#1173, opencode steal — <c>cycleSessionTab</c> with an unread
    ///     matcher). Wraps around tab order; the <paramref name="from" /> tab
    ///     itself is never the answer, so pressing next-unread while looking at
    ///     the only unread tab is a no-op instead of an acknowledge.
    /// </summary>
    /// <param name="from">The tab to search from (usually the active one), or null to scan from an end.</param>
    /// <param name="forward">True for next, false for previous.</param>
    /// <returns>The unread tab's session id, or null when no OTHER tab is unread.</returns>
    public SessionId? NextUnread(SessionId? from, bool forward)
    {
        var tabs = Tabs;
        if (tabs.Length == 0)
            return null;

        int start = from is { } id ? IndexOf(id) : -1;
        for (int step = 1; step <= tabs.Length; step++)
        {
            int candidate = start < 0
                ? (forward ? step - 1 : tabs.Length - step)
                : (((start + (forward ? step : -step)) % tabs.Length + tabs.Length) % tabs.Length);
            if (candidate == start)
                continue;
            if (tabs[candidate].HasUnread)
                return tabs[candidate].SessionId;
        }

        return null;
    }
}
