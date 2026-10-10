using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Application.Sessions;

/// <summary>
///     Lifecycle of an isolated run (epic #42). S2 materializes
///     <c>Isolated</c> and releases to <c>Released</c>; the full S9 chain
///     (slices S3–S8, #397) moves through the states between. The legal
///     transitions live in <see cref="RunChangeTransitions" /> — extend the
///     map there, never reinterpret a state here.
/// </summary>
public enum RunState
{
    /// <summary>The contract is pinned (S1) but no worktree exists yet.</summary>
    Pinned,

    /// <summary>The worktree is materialized and belongs to the run.</summary>
    Isolated,

    /// <summary>The agent's work is frozen into an immutable change set (S3).</summary>
    Changed,

    /// <summary>Declared checks ran against the frozen head (S4).</summary>
    Checked,

    /// <summary>The verification report is rendered from recorded artifacts (S5).</summary>
    Reported,

    /// <summary>The operator accepted the change into their tree (S6).</summary>
    Accepted,

    /// <summary>The operator rejected the run in one of the three modes (S7).</summary>
    Rejected,

    /// <summary>The worktree was removed; the manifest is a tombstone.</summary>
    Released,
}

/// <summary>
///     An isolated working copy produced from a <see cref="WorkspaceContract" />:
///     a detached git worktree checked out at the pinned revision.
/// </summary>
/// <param name="RunId">Minted id; the only path input (no user path survives).</param>
/// <param name="Path">Absolute worktree path (<c>runs/&lt;RunId&gt;/worktree</c>).</param>
/// <param name="BaseRevision">Commit sha the worktree was pinned at.</param>
public sealed record IsolatedWorkspace(
    RunId RunId,
    string Path,
    string BaseRevision);

/// <summary>
///     Run manifest (epic #42, stage S2): everything a later process needs to
///     act on the run — the contract fields verbatim, the worktree path and
///     the lifecycle state. Persisted as JSON at
///     <c>runs/&lt;RunId&gt;/manifest.json</c>, written atomically (temp +
///     rename), so accept/reject/report (S5–S7) can run after the operator
///     closes the terminal and comes back.
/// </summary>
/// <param name="RunId">Minted run id (matches the directory name).</param>
/// <param name="RepoRoot">Repository root the contract was pinned against.</param>
/// <param name="BaseRevision">Pinned commit sha.</param>
/// <param name="BaseBranch">Pinned branch; null on detached HEAD.</param>
/// <param name="Isolation">Isolation strategy (always <c>Worktree</c> in v1).</param>
/// <param name="TimeoutSeconds">Wall-clock budget from the contract limits.</param>
/// <param name="MaxSteps">Max agent steps from the contract limits.</param>
/// <param name="WorktreePath">Absolute path of the materialized worktree.</param>
/// <param name="State">Lifecycle state.</param>
/// <param name="CreatedAtUtc">Manifest creation time.</param>
public sealed record RunManifest(
    string RunId,
    string RepoRoot,
    string BaseRevision,
    string? BaseBranch,
    WorkspaceIsolation Isolation,
    int TimeoutSeconds,
    int MaxSteps,
    string WorktreePath,
    RunState State,
    DateTimeOffset CreatedAtUtc);
