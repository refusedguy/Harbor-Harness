using Harbor.Abstractions.Git;

namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Session-side seed row. The host maps this from <c>SessionInfo</c> plus
///     read-only <c>ISessionManager</c> lookups (<c>GetContext</c> /
///     <c>GetGitInfo</c> — never mutated here); the seeder stays BCL-pure so
///     the merge is unit-testable without any session infrastructure.
/// </summary>
/// <param name="SessionId">Stable session id passed to <c>OpenSessionAsync</c> on confirm.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="Directory">Absolute working directory of the session (may be empty when unknown).</param>
/// <param name="Branch">Git branch of the session directory, or null when unknown.</param>
/// <param name="StatusText">Agent status display text (<c>"idle"</c>, <c>"working"</c>, …).</param>
/// <param name="IsDirty">Whether the working tree has uncommitted changes.</param>
/// <param name="IsSubagent">Whether this is an isolated sub-agent run (hidden from the palette by default).</param>
public sealed record SessionSeed(
    string SessionId,
    string Title,
    string Directory,
    string? Branch,
    string StatusText,
    bool IsDirty,
    bool IsSubagent = false);

/// <summary>
///     Pure jump-palette seeding (KILLER_FEATURES §2.7 Feature 3, slice 2):
///     merges the linked working trees of the current repository with the
///     active sessions into <see cref="WorktreeJumpEntry" /> rows. BCL-plus-
///     contracts, zero Harbor dependencies beyond the Domain records it is
///     handed — it reads nothing, spawns nothing, and the panel owns both.
/// </summary>
/// <remarks>
///     <para>
///         <b>#666: the porcelain parser left this file.</b> It used to sit here as
///         <c>ParsePorcelain</c>, beside the panel that forked <c>git</c>, and it is now
///         <c>WorktreePorcelainParser</c> in <c>Harbor.Application</c> — next to the
///         <c>Process.Start</c> whose output it reads. The panel asks
///         <see cref="IGitQuery" /> for <see cref="GitWorktreeInfo" /> rows instead of
///         forking <c>git worktree list --porcelain</c> and interpreting the text here,
///         so git's wire format no longer crosses into a Presentation assembly. What
///         stayed is the merge, which was always clean and never spawned anything.
///     </para>
/// </remarks>
public static class WorktreeJumpSeeder
{
    /// <summary>Status text for worktrees that have no session to switch to.</summary>
    public const string NoSessionStatus = "no-session";

    /// <summary>
    ///     Merge sessions with worktrees into palette rows. Sessions keep their
    ///     incoming order; each row's branch prefers the session record and falls
    ///     back to the worktree list matched by directory. Worktrees with no
    ///     session are appended (path-sorted) with an empty
    ///     <see cref="WorktreeJumpEntry.SessionId" /> — Enter on those rows only
    ///     closes the palette since there is no session to switch to. Bare
    ///     records never become rows. Sub-agent sessions are skipped unless
    ///     <paramref name="includeSubagents" /> is set (they live in the
    ///     <c>subagents</c> panel instead).
    /// </summary>
    public static IReadOnlyList<WorktreeJumpEntry> BuildEntries(
        IReadOnlyList<SessionSeed> sessions,
        IReadOnlyList<GitWorktreeInfo> worktrees,
        bool includeSubagents = false)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(worktrees);

        var byPath = new Dictionary<string, GitWorktreeInfo>(StringComparer.Ordinal);
        for (int i = 0; i < worktrees.Count; i++)
        {
            var wt = worktrees[i];
            if (wt.IsBare || string.IsNullOrEmpty(wt.Path))
            {
                continue;
            }

            byPath[wt.Path] = wt;
        }

        var entries = new List<WorktreeJumpEntry>(sessions.Count + worktrees.Count);
        for (int i = 0; i < sessions.Count; i++)
        {
            var seed = sessions[i];
            if (seed.IsSubagent && !includeSubagents)
                continue;
            string? branch = seed.Branch;
            if (string.IsNullOrEmpty(branch)
                && !string.IsNullOrEmpty(seed.Directory)
                && byPath.TryGetValue(seed.Directory, out var wt))
            {
                branch = wt.Branch;
            }

            string status = seed.IsDirty ? seed.StatusText + " ●" : seed.StatusText;
            entries.Add(new WorktreeJumpEntry(seed.SessionId, seed.Title, seed.Directory, branch, status));
            if (!string.IsNullOrEmpty(seed.Directory))
            {
                byPath.Remove(seed.Directory);
            }
        }

        var rest = new List<GitWorktreeInfo>(byPath.Values);
        rest.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        for (int i = 0; i < rest.Count; i++)
        {
            var wt = rest[i];
            string title = Path.GetFileName(wt.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(title))
            {
                title = wt.Path;
            }

            entries.Add(new WorktreeJumpEntry(string.Empty, title, wt.Path, wt.Branch, NoSessionStatus));
        }

        return entries;
    }
}
