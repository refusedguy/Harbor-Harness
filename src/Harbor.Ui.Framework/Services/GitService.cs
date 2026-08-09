using System.Diagnostics;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Services;
/// <summary>
///     Fetches git status for a working directory — branch name + dirty/clean.
///     Runs `git rev-parse --abbrev-ref HEAD` and `git status --porcelain`.
///     Returns (null, false) if the directory is not a git repo.
/// </summary>
public sealed class GitService
{
    private readonly ILogger<GitService> _logger;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
    }

    /// <summary>Get git info for a directory.</summary>
    public GitSessionInfo GetGitStatus(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return GitSessionInfo.Empty;

        try
        {
            string? branch = RunGit(directory, "rev-parse", "--abbrev-ref", "HEAD");
            if (string.IsNullOrEmpty(branch))
                return GitSessionInfo.Empty;

            string? status = RunGit(directory, "status", "--porcelain");
            bool isDirty = !string.IsNullOrEmpty(status?.Trim());
            int dirtyCount = isDirty ? status!.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;

            string? lastCommit = RunGit(directory, "log", "-1", "--format=%cr");
            string? commitTime = null;
            if (!string.IsNullOrEmpty(lastCommit))
            {
                commitTime = lastCommit.Trim();
            }

            return new GitSessionInfo(branch.Trim(), isDirty, dirtyCount, commitTime);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Git status failed for {Dir}", directory);
            return GitSessionInfo.Empty;
        }
    }

    private static string? RunGit(string workingDir, params string[] args)
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
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process is null) return null;
        if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
        {
            try { process.Kill(); }
            catch
            { /* process already exited */
            }
            return null;
        }
        if (process.ExitCode != 0) return null;
        return process.StandardOutput.ReadToEnd();
    }
}
