namespace Harbor.Ui.Framework.Panels;

/// <summary>
///     Narrow, panel-facing projection of live session state (#470).
/// </summary>
/// <remarks>
///     <para>
///         Framework panels need a handful of per-session facts, but the real
///         owner (<c>Harbor.Ui.Framework.Sessions.ISessionManager</c>) lives in an
///         assembly that already depends on this one — naming it from
///         <see cref="PanelContext" /> would invert the layer. This interface
///         is the seam: the composition root adapts the session manager to it
///         once, and panels read flat primitives instead of reaching into
///         <c>SessionContext</c> / <c>GitSessionInfo</c>.
///     </para>
///     <para>
///         Every member is total: an unknown session returns a null/empty value
///         rather than throwing, so a panel degrades row-by-row instead of
///         failing the whole frame.
///     </para>
/// </remarks>
public interface IPanelSessionGateway
{
    /// <summary>Working directory of the session, or <see langword="null" /> when unknown.</summary>
    /// <param name="sessionId">Session id.</param>
    string? GetDirectory(string sessionId);

    /// <summary>Live status line (e.g. <c>"running"</c>), or <see langword="null" /> when unknown.</summary>
    /// <param name="sessionId">Session id.</param>
    string? GetStatusText(string sessionId);

    /// <summary>
    ///     Current git branch — cached git info first, then the session record —
    ///     or <see langword="null" /> when the directory is not a repo.
    /// </summary>
    /// <param name="sessionId">Session id.</param>
    string? GetBranch(string sessionId);

    /// <summary>
    ///     Whether the working tree has uncommitted changes. Unknown sessions
    ///     report <see langword="false" /> — the same coercion the jump palette
    ///     applied to its old <c>manager?.GetGitInfo(id)?.IsDirty ?? false</c> chain.
    /// </summary>
    /// <param name="sessionId">Session id.</param>
    bool GetIsDirty(string sessionId);

    /// <summary>Whether the session is a sub-agent, or <see langword="null" /> when unknown.</summary>
    /// <param name="sessionId">Session id.</param>
    bool? GetIsSubagent(string sessionId);

    /// <summary>
    ///     Switch the host to <paramref name="sessionId" />; <see langword="false" /> when
    ///     unknown.
    /// </summary>
    /// <remarks>
    ///     Deliberately <b>not</b> named <c>OpenSessionAsync</c>: the session facade
    ///     already exposes that name on its lifecycle interface, and an
    ///     implementation inheriting both would make every call ambiguous
    ///     (Sonar S3444). Same operation, panel-facing name.
    /// </remarks>
    /// <param name="sessionId">Session id to open.</param>
    Task<bool> OpenPanelSessionAsync(string sessionId);
}
