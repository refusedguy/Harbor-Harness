using Harbor.Abstractions.Git;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Reactive wrapper that adapts <see cref="IGitQuery" /> to the session
///     view-model's <see cref="GitSessionInfo" />. It no longer touches the
///     filesystem or forks a process (#537): the query contract is Domain, the
///     process spawn is Application, and this type only maps one to the other.
/// </summary>
/// <remarks>
///     <para>
///         <b>Permission gating — a decision, not an oversight.</b> This is UI
///         chrome: it renders a branch badge for a directory the user opened, is
///         driven by no model, and every query behind <see cref="IGitQuery" /> is
///         read-only. It is therefore NOT routed through
///         <c>PermissionRuleset</c>: prompting the user to approve reading their own
///         branch name would be noise, and the commands carry no agent intent. The
///         agent's own git access is a different path entirely — it goes through the
///         <c>bash</c> tool, where <c>PermissionRuleset.Default</c> allows
///         <c>git status</c>/<c>git diff</c>/<c>git log</c> and asks on anything
///         else. <c>GitServicePermissionGatingTests</c> pins both halves.
///     </para>
/// </remarks>
public sealed class GitService
{
    private readonly IGitQuery _queries;
    private readonly ILogger<GitService> _logger;

    /// <summary>Construct a wrapper over the injected git query seam.</summary>
    /// <param name="queries">The read-only git queries (Domain contract).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public GitService(IGitQuery queries, ILogger<GitService> logger)
    {
        _queries = queries;
        _logger = logger;
    }

    /// <summary>Get git info for a directory.</summary>
    public GitSessionInfo GetGitStatus(string directory)
    {
        try
        {
            GitWorkspaceStatus status = _queries.GetStatus(directory);
            if (string.IsNullOrEmpty(status.Branch))
            {
                return GitSessionInfo.Empty;
            }

            return new GitSessionInfo(
                status.Branch,
                status.IsDirty,
                status.DirtyFileCount,
                status.LastCommitRelative);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Git status failed for {Dir}", directory);
            return GitSessionInfo.Empty;
        }
    }
}
