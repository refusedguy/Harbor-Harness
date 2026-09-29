using System.Diagnostics;
using Harbor.Abstractions.Git;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Git;

/// <summary>
///     <see cref="IGitQuery" /> over the <c>git</c> binary. The process spawn lives
///     here, beside <c>BashTool</c> and <c>WorkspaceInspector</c>, so the
///     Presentation assembly that renders the branch badge no longer forks anything
///     (#537 / <c>PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES</c>).
/// </summary>
/// <remarks>
///     <para>
///         Read-only by construction: the only argument vectors it ever builds are
///         <c>rev-parse --abbrev-ref HEAD</c>, <c>status --porcelain</c> and
///         <c>log -1 --format=%cr</c>. There is no <c>push</c>/<c>commit</c>/<c>reset</c>
///         path here, and <see cref="GetStatus" /> takes no free-form command string, so
///         this type cannot be turned into a general shell-out.
///     </para>
///     <para>
///         Not an <c>ITool</c>, and deliberately so: it serves UI chrome for a
///         directory the user opened, takes no model input, and every command it runs
///         is read-only. Agent-driven git goes through <c>bash</c>, where
///         <c>PermissionRuleset.Default</c> gates it.
///     </para>
/// </remarks>
public sealed class ProcessGitQuery(ILogger<ProcessGitQuery> logger) : IGitQuery
{
    /// <summary>Hard ceiling on a single git invocation, so a hung git cannot wedge a UI refresh.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return GitWorkspaceStatus.None;
        }

        try
        {
            string? branch = RunGit(directory, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
            if (string.IsNullOrEmpty(branch))
            {
                return GitWorkspaceStatus.None;
            }

            string? status = RunGit(directory, cancellationToken, "status", "--porcelain");
            bool isDirty = !string.IsNullOrEmpty(status?.Trim());
            int dirtyCount = isDirty
                ? status!.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

            string? lastCommit = RunGit(directory, cancellationToken, "log", "-1", "--format=%cr");

            return new GitWorkspaceStatus(
                branch.Trim(),
                isDirty,
                dirtyCount,
                string.IsNullOrEmpty(lastCommit) ? null : lastCommit.Trim());
        }
        catch (Exception ex)
        {
            // Not a repository, git missing from PATH, or a timeout: all expected
            // outcomes for a UI badge, not exceptions for the caller to handle.
            logger.LogDebug(ex, "Git status failed for {Dir}", directory);
            return GitWorkspaceStatus.None;
        }
    }

    /// <summary>
    ///     Runs one fixed git argument vector and returns its stdout, or
    ///     <see langword="null" /> when git is missing, times out, or exits non-zero.
    /// </summary>
    private static string? RunGit(string workingDir, CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        if (!process.WaitForExit(Timeout))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited between the timeout and the kill — nothing to do.
            }

            return null;
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        // The reader is only safe once the process has exited and the pipe is
        // drained; cancellation before that is handled by the timeout above.
        cancellationToken.ThrowIfCancellationRequested();
        return process.StandardOutput.ReadToEnd();
    }
}
