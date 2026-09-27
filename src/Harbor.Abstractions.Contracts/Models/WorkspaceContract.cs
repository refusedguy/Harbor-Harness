namespace Harbor.Abstractions.Models;

/// <summary>
///     Isolation strategy for an agent run's working copy (epic #42, stage S1).
/// </summary>
public enum WorkspaceIsolation
{
    /// <summary>Plain directory copy of the base revision.</summary>
    Copy,

    /// <summary>Git worktree checked out at the base revision.</summary>
    Worktree,
}

/// <summary>
///     Execution bounds pinned into the workspace contract before a run starts.
/// </summary>
/// <param name="TimeoutSeconds">Wall-clock budget for the run, in seconds (S1 default: 600).</param>
/// <param name="MaxSteps">Maximum agent steps (LLM turns) for the run (S1 default: 50).</param>
public sealed record WorkspaceLimits(
    int TimeoutSeconds = 600,
    int MaxSteps = 50);

/// <summary>
///     Workspace contract (epic #42, stage S1): the pinned preconditions of an
///     isolated agent run, captured before the run starts and printed for the
///     operator. Everything is init-only — the contract is a snapshot, mutated
///     exclusively through <c>with</c> expressions.
/// </summary>
/// <param name="RepoRoot">Absolute path of the repository root the run was pinned against.</param>
/// <param name="BaseRevision">Full commit sha from <c>git rev-parse HEAD</c> at pin time.</param>
/// <param name="BaseBranch">Branch from <c>git branch --show-current</c>; null on detached HEAD.</param>
/// <param name="TrackedDirt">
///     Dirty tracked paths from <c>git status --porcelain=v1</c>. Empty on every
///     successful pin — any tracked dirt rejects the run instead of being pinned.
/// </param>
/// <param name="UntrackedPresent">
///     Whether untracked files exist. Flag only — untracked paths are never
///     enumerated (S1 records presence, not inventory).
/// </param>
/// <param name="Isolation">How the isolated working copy is produced.</param>
/// <param name="Limits">Execution bounds for the run.</param>
public sealed record WorkspaceContract(
    string RepoRoot,
    string BaseRevision,
    string? BaseBranch,
    IReadOnlyList<string> TrackedDirt,
    bool UntrackedPresent,
    WorkspaceIsolation Isolation,
    WorkspaceLimits Limits);
