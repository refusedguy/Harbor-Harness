namespace Harbor.Abstractions.Git;

/// <summary>
///     Read-only git facts about one working directory — branch, dirty flag, dirty
///     file count, and the relative time of the last commit.
/// </summary>
/// <param name="Branch">The current branch name, or <see langword="null" /> when the directory is not a git repo.</param>
/// <param name="IsDirty">Whether the working tree has uncommitted changes.</param>
/// <param name="DirtyFileCount">How many porcelain status lines the working tree reports.</param>
/// <param name="LastCommitRelative">The last commit's relative timestamp (git's <c>%cr</c>), when there is one.</param>
public sealed record GitWorkspaceStatus(string? Branch, bool IsDirty, int DirtyFileCount, string? LastCommitRelative)
{
    /// <summary>Empty / not-a-repo sentinel.</summary>
    public static GitWorkspaceStatus None { get; } = new(null, false, 0, null);
}

/// <summary>
///     One linked working tree of the repository that contains
///     <c>directory</c> — either the main checkout or a <c>git worktree add</c> entry.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the record is here and not in the consumer.</b> #666: the
///         jump-palette panel forked <c>git worktree list --porcelain</c> from a
///         Presentation assembly and parsed the output in
///         <c>WorktreeJumpSeeder</c>, which lives in <c>Harbor.Ui.Framework.Services</c>.
///         Application cannot reference that assembly, so a contract returning
///         porcelain text would leak the wire format through a Domain interface; and
///         a record left in the palette's assembly would give each side its own copy
///         of the same three fields. One record, declared next to
///         <see cref="GitWorkspaceStatus" />, keeps parsing on the Application side
///         where <c>ProcessGitQuery</c> already reads git's output.
///     </para>
/// </remarks>
/// <param name="Path">Absolute path of the working tree.</param>
/// <param name="Branch">Short branch name, or <see langword="null" /> when detached.</param>
/// <param name="IsBare">True for a <c>bare</c> entry — a repository store rather than a checkout, and never a jump target.</param>
public sealed record GitWorktreeInfo(string Path, string? Branch, bool IsBare);

/// <summary>
///     Read-only git queries for a working directory (issue #537).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this contract exists.</b> <c>GitService</c> used to fork
///         <c>git</c> from a Presentation assembly, which put it outside the
///         <c>ITool</c>/<c>PermissionRuleset</c> seam the rest of the harness is
///         built on, made it untestable (no seam to inject a fake process runner) and
///         uncancellable. The narrow query contract lives in Domain; the process
///         spawn lives with the rest of the shell-outs in Application, where
///         <c>BashTool</c> and <c>WorkspaceInspector</c> already fork.
///     </para>
///     <para>
///         <b>Permission gating is a deliberate, separate decision.</b> The agent
///         drives git through the <c>bash</c> tool, where
///         <c>PermissionRuleset.Default</c> gates <c>git status</c> / <c>git diff</c> /
///         <c>git log</c> and asks on anything else — that path IS gated. This
///         contract is the UI chrome: it renders the branch badge next to a session
///         the user opened themselves, takes no model input, runs three read-only
///         commands (<c>rev-parse</c>, <c>status --porcelain</c>, <c>log -1</c>) and
///         mutates nothing, so putting it behind the agent approval gate would
///         prompt the user to approve reading their own branch name. The decision is
///         pinned by <c>GitServicePermissionGatingTests</c> so it cannot quietly
///         become an oversight.
///     </para>
/// </remarks>
public interface IGitQuery
{
    /// <summary>
    ///     Branch, dirty state and last-commit age for <paramref name="directory" />.
    ///     Returns <see cref="GitWorkspaceStatus.None" /> for a missing directory or a
    ///     non-repository — never throws for an expected failure.
    /// </summary>
    /// <param name="directory">The working directory to inspect.</param>
    /// <param name="cancellationToken">Cancels the git invocation.</param>
    GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every linked working tree of the repository that contains
    ///     <paramref name="directory" />, main checkout first, as git reports
    ///     them. Returns an empty list for a missing directory, a non-repository,
    ///     or a git that is missing, times out or exits non-zero — never throws
    ///     for an expected failure.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Added in #666. The jump palette needs this and had no way to ask:
    ///         it built its own <c>ProcessStartInfo</c> for
    ///         <c>git worktree list --porcelain</c> inside a Presentation assembly.
    ///         That is the same second-path defect #537 fixed for the branch badge,
    ///         so this is a method on the port that already existed rather than a
    ///         second port.
    ///     </para>
    ///     <para>
    ///         <b>Read-only, like every other method here.</b> The only argument
    ///         vector this can produce is <c>worktree list --porcelain</c>; there is
    ///         no <c>add</c>/<c>remove</c>/<c>prune</c> path, and no free-form command
    ///         string on the contract, so it cannot be turned into a general
    ///         shell-out. The permission-gating reasoning in the remarks above is
    ///         unchanged: this is UI chrome over a directory the user opened, takes
    ///         no model input, and mutates nothing. The agent's git access still
    ///         goes through the <c>bash</c> tool and <c>PermissionRuleset</c>.
    ///     </para>
    /// </remarks>
    /// <param name="directory">A directory inside the repository to list worktrees for.</param>
    /// <param name="cancellationToken">Cancels the git invocation.</param>
    IReadOnlyList<GitWorktreeInfo> ListWorktrees(string directory, CancellationToken cancellationToken = default);
}
