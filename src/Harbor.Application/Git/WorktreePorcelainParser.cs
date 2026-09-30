using Harbor.Abstractions.Git;

namespace Harbor.Application.Git;

/// <summary>
///     Parses <c>git worktree list --porcelain</c> output into
///     <see cref="GitWorktreeInfo" /> records (#666).
/// </summary>
/// <remarks>
/// <para>
///     This is <c>WorktreeJumpSeeder.ParsePorcelain</c>, moved. The parser was
///     always pure — BCL-only, no Harbor dependencies, no process — but it lived
///     in <c>Harbor.Ui.Framework.Services</c> because that is where the spawn was,
///     and the spawn was the thing that had to move. Leaving the parser behind
///     would have meant one of two worse shapes: the Domain contract returning
///     porcelain text so <c>ProcessGitQuery</c> could hand the wire format back
///     to a Presentation assembly to interpret, or a second copy of the parser
///     next to the one that already existed.
/// </para>
/// <para>
///     Public rather than internal, and that is deliberate: the parse is the
///     part worth pinning, and <c>Harbor.Application</c> exposes
///     <c>InternalsVisibleTo</c> only to the benchmarks. A parser with no test is
///     a parser nobody is watching.
/// </para>
/// <para>
///     The state machine is unchanged from the original — it was already correct,
///     including its tolerance of CRLF and of unrecognised lines.
/// </para>
/// </remarks>
public static class WorktreePorcelainParser
{
    /// <summary>
    ///     Parse <c>git worktree list --porcelain</c> output into records, in the
    ///     order git emitted them (the main checkout first). Malformed lines are
    ///     ignored; a null/empty input yields no records.
    /// </summary>
    /// <param name="output">Raw stdout of the porcelain listing, possibly null.</param>
    public static IReadOnlyList<GitWorktreeInfo> Parse(string? output)
    {
        var infos = new List<GitWorktreeInfo>();
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
                infos.Add(new GitWorktreeInfo(path, branch, bare));
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
}
