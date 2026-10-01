using Harbor.Abstractions.Git;
using Harbor.Abstractions.Permissions;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #537: <c>GitService</c> forked <c>git</c> from a Presentation assembly
///     and bypassed the <c>ITool</c>/permission seam entirely. It is now a thin
///     mapper over the Domain <see cref="IGitQuery" /> contract, which is what makes
///     it testable at all — and the process spawn moved to
///     <c>Harbor.Application.Git.ProcessGitQuery</c>, deleting the two
///     <c>PresentationCapabilityRules.KnownViolations</c> rows.
/// </summary>
/// <remarks>
///     <para>
///         One question survives the refactor, and it must not be an accident: is the
///         UI-chrome git read permission-gated? These tests pin the answer, both
///         halves of it:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Not gated, deliberately.</b> No model drives it, every query is
///             read-only, and it runs against a directory the user opened. Prompting
///             to read one's own branch name would be noise. That is a DECISION, and
///             the architectural layer (not <c>PermissionRuleset</c>) is what now
///             enforces the part that can be enforced: Presentation no longer forks.
///         </item>
///         <item>
///             <b>The agent's git IS gated</b>, through the <c>bash</c> tool:
///             <c>PermissionRuleset.Default</c> allows <c>git status</c>,
///             <c>git diff</c> and <c>git log</c>, asks on anything else, and denies
///             the destructive set. The two paths were never the same path; the bug
///             was that the second one had no gate AT ALL. These assertions make the
///             gate's existence a test fact rather than a reading of a ruleset dump.
///         </item>
///     </list>
/// </remarks>
public class GitServicePermissionGatingTests
{
    // ── The seam: GitService is now testable without a git binary ─────────

    [Test]
    public async Task GetGitStatus_Maps_The_Query_Result_Onto_The_Session_Snapshot()
    {
        var service = new GitService(
            new StubGitQuery(new GitWorkspaceStatus("main", true, 3, "2 hours ago")),
            NullLogger<GitService>.Instance);

        var info = service.GetGitStatus("/any/dir");

        await Assert.That(info.Branch).IsEqualTo("main");
        await Assert.That(info.IsDirty).IsTrue();
        await Assert.That(info.DirtyCount).IsEqualTo(3);
        await Assert.That(info.LastCommit).IsEqualTo("2 hours ago");
    }

    [Test]
    public async Task GetGitStatus_NonRepository_IsThe_Empty_Sentinel()
    {
        var service = new GitService(
            new StubGitQuery(GitWorkspaceStatus.None),
            NullLogger<GitService>.Instance);

        var info = service.GetGitStatus("/not/a/repo");

        await Assert.That(info).IsEqualTo(GitSessionInfo.Empty);
    }

    [Test]
    public async Task GetGitStatus_Query_Throws_Yields_Empty_Instead_Of_Throwing()
    {
        var service = new GitService(
            new ThrowingGitQuery(),
            NullLogger<GitService>.Instance);

        var info = service.GetGitStatus("/any/dir");

        await Assert.That(info).IsEqualTo(GitSessionInfo.Empty)
            .Because("a badge must never take down a session switch");
    }

    // ── The decision: the AGENT's git is permission-gated ─────────────────

    [Test]
    public async Task Agent_Git_IsGated_Through_Bash_ReadOnly_Subcommands_AreAllowed()
    {
        // The gate the audit said was missing. `git` reaches the agent only as a
        // `bash` argument, and these three read-only spellings are the allow-listed
        // ones in PermissionRuleset.Default.
        await Assert.That(PermissionRuleset.Default.Evaluate("bash", "git status"))
            .IsEqualTo(PermissionAction.Allow);
        await Assert.That(PermissionRuleset.Default.Evaluate("bash", "git diff --stat"))
            .IsEqualTo(PermissionAction.Allow);
        await Assert.That(PermissionRuleset.Default.Evaluate("bash", "git log --oneline -10"))
            .IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Agent_Git_IsGated_Writing_Subcommands_AreNotSilentlyAllowed()
    {
        // Not Allow is the honest assertion: whether the verdict is Ask or Deny, the
        // important property is that it is not a blanket permission for `git *`.
        await Assert.That(PermissionRuleset.Default.Evaluate("bash", "git push origin main"))
            .IsNotEqualTo(PermissionAction.Allow);
        await Assert.That(PermissionRuleset.Default.Evaluate("bash", "git reset --hard HEAD~5"))
            .IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Agent_Git_IsGated_Destructive_Git_Command_IsDenied_NotAsked()
    {
        // The command-shape guard reaches git invocations too, because the arg
        // bash evaluates is the whole command line.
        await Assert.That(PermissionRuleset.Default.Evaluate("bash", "sudo git push --force"))
            .IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Agent_Git_IsGated_There_IsNo_Bare_Git_Tool_Bypassing_Bash()
    {
        // The other half of the decision: the agent's git access is `bash`, and
        // nothing else. A tool named `git` would be a second, ungated door, so its
        // absence is the property worth pinning — the plugin sample GitTools ships
        // as a PLUGIN, never as a builtin.
        var gateApplies = PermissionRuleset.Default.SafetyPolicies
            .Any(policy => policy.AppliesTo("git"));
        await Assert.That(gateApplies).IsFalse()
            .Because("no builtin is named 'git'; agent git access is the bash tool, "
                     + "and the path guard must not claim a tool that does not exist");
    }

    // ── Doubles ──────────────────────────────────────────────────────────

    private sealed class StubGitQuery(GitWorkspaceStatus result) : IGitQuery
    {
        public string? LastDirectory { get; private set; }

        public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
        {
            LastDirectory = directory;
            return result;
        }

        // #666 added ListWorktrees for the jump palette. These tests are about
        // GetStatus and the branch badge, so the worktree half answers nothing
        // rather than pretending to be a git repo.
        public IReadOnlyList<GitWorktreeInfo> ListWorktrees(
            string directory,
            CancellationToken cancellationToken = default)
            => Array.Empty<GitWorktreeInfo>();
    }

    private sealed class ThrowingGitQuery : IGitQuery
    {
        public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("git is not on PATH");

        public IReadOnlyList<GitWorktreeInfo> ListWorktrees(
            string directory,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("git is not on PATH");
    }
}
