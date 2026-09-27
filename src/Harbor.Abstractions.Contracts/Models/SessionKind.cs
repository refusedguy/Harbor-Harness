namespace Harbor.Abstractions.Models;

/// <summary>
///     Ownership kind of a session: a normal user-driven conversation, or an
///     isolated sub-agent run spawned by the <c>task</c> tool.
/// </summary>
/// <remarks>
///     Stamped by <c>SubAgentRunner</c> when it creates the child session.
///     Records persisted before this field existed deserialize as
///     <see cref="SessionKind.User" /> — see
///     <see cref="SessionKindExtensions.IsSubagent" /> for the legacy fallback.
/// </remarks>
public enum SessionKind
{
    /// <summary>Normal user-driven session.</summary>
    User = 0,

    /// <summary>Isolated sub-agent session (hidden from the jump palette by default).</summary>
    Subagent = 1,
}

/// <summary>
///     Classification helpers for <see cref="SessionKind" />.
/// </summary>
public static class SessionKindExtensions
{
    /// <summary>
    ///     Title marker stamped on spawned sub-agent sessions
    ///     (<c>task(agent-name): first line…</c>).
    /// </summary>
    public const string SubagentTitlePrefix = "task(";

    /// <summary>
    ///     Whether the session is a sub-agent run: an explicit
    ///     <see cref="SessionKind.Subagent" /> stamp, or — for records persisted
    ///     before <c>Session.Kind</c> existed — the legacy linkage shape (a
    ///     parent id plus the <c>task(…)</c> title marker). Forks/branches carry
    ///     a parent id but never the marker, so they still classify as user.
    /// </summary>
    /// <param name="session">The session to classify.</param>
    /// <returns>True for sub-agent sessions.</returns>
    public static bool IsSubagent(this Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Kind == SessionKind.Subagent)
            return true;
        return session.ParentSessionId is not null
            && session.Title.StartsWith(SubagentTitlePrefix, StringComparison.Ordinal);
    }
}
