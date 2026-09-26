namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     One parsed <c>git worktree list --porcelain</c> record: a linked working
///     tree (or the main checkout) with its branch. Bare repos carry
///     <see cref="IsBare" /> and never become jump targets.
/// </summary>
/// <param name="Path">Absolute worktree directory.</param>
/// <param name="Branch">Short branch name (<c>null</c> when detached or bare).</param>
/// <param name="IsBare">True for <c>bare</c> records (the repo store, not a checkout).</param>
public sealed record WorktreeInfo(string Path, string? Branch, bool IsBare);

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
public sealed record SessionSeed(
    string SessionId,
    string Title,
    string Directory,
    string? Branch,
    string StatusText,
    bool IsDirty);

/// <summary>
///     Pure jump-palette seeding (KILLER_FEATURES §2.7 Feature 3, slice 2):
///     parses <c>git worktree list --porcelain</c> output and merges it with the
///     active sessions into <see cref="WorktreeJumpEntry" /> rows. BCL-only, zero
///     Harbor dependencies — the panel owns process spawning and session reads.
/// </summary>
public static class WorktreeJumpSeeder
{
    /// <summary>Status text for worktrees that have no session to switch to.</summary>
    public const string NoSessionStatus = "no-session";

    /// <summary>
    ///     Parse <c>git worktree list --porcelain</c> output into records.
    ///     Malformed lines are ignored; a null/empty input yields no records.
    /// </summary>
    public static IReadOnlyList<WorktreeInfo> ParsePorcelain(string? output)
    {
        var infos = new List<WorktreeInfo>();
        if (string.IsNullOrEmpty(output))
        {
            return infos;
        }

        string? path = null;
        string? branch = null;
        bool bare = false;
        void Flush()
        {
            if (!string.IsNullOrEmpty(path))
            {
                infos.Add(new WorktreeInfo(path, branch, bare));
            }

            path = null;
            branch = null;
            bare = false;
        }

        // Split on '\n' and trim a trailing '\r' per line (git emits LF; tolerate CRLF).
        int start = 0;
        for (int i = 0; i <= output.Length; i++)
        {
            bool end = i == output.Length || output[i] == '\n';
            if (!end)
            {
                continue;
            }

            int len = i - start;
            if (len > 0 && output[i - 1] == '\r')
            {
                len--;
            }

            string line = len > 0 ? output.Substring(start, len) : string.Empty;
            start = i + 1;
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                string candidate = line.Substring("worktree ".Length).Trim();
                path = candidate.Length > 0 ? candidate : null;
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                string candidate = line.Substring("branch refs/heads/".Length).Trim();
                branch = candidate.Length > 0 ? candidate : null;
            }
            else if (line == "bare")
            {
                bare = true;
            }
        }

        Flush();
        return infos;
    }

    /// <summary>
    ///     Merge sessions with worktrees into palette rows. Sessions keep their
    ///     incoming order; each row's branch prefers the session record and falls
    ///     back to the worktree list matched by directory. Worktrees with no
    ///     session are appended (path-sorted) with an empty
    ///     <see cref="WorktreeJumpEntry.SessionId" /> — Enter on those rows only
    ///     closes the palette since there is no session to switch to. Bare
    ///     records never become rows.
    /// </summary>
    public static IReadOnlyList<WorktreeJumpEntry> BuildEntries(
        IReadOnlyList<SessionSeed> sessions,
        IReadOnlyList<WorktreeInfo> worktrees)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(worktrees);

        var byPath = new Dictionary<string, WorktreeInfo>(StringComparer.Ordinal);
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

        var rest = new List<WorktreeInfo>(byPath.Values);
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
