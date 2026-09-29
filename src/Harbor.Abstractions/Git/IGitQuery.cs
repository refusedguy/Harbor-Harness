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
}
