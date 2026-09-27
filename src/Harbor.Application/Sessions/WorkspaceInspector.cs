using System.Diagnostics;
using System.Text;

namespace Harbor.Application.Sessions;

/// <summary>
///     Workspace inspector (epic #42, stage S1): pins the <see cref="WorkspaceContract" />
///     via git-only probes against a repository root. No filesystem inventory,
///     no hashing — the three git calls below are the entire surface.
/// </summary>
/// <remarks>
///     <para>
///         Fail-closed: a missing git binary, a non-repository directory, any
///         non-zero git exit, a per-call timeout, or any dirty <i>tracked</i>
///         path yields <c>Result.Failure</c> with the reason — the
///         run must be rejected, never pinned over dirty state. Untracked files
///         are tolerated and surface only as <see cref="WorkspaceContract.UntrackedPresent" />.
///     </para>
/// </remarks>
public static class WorkspaceInspector
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);
    private const int MaxDirtPathsInError = 20;
    private const int MaxStderrInError = 500;

    /// <summary>
    ///     Pin the workspace contract for <paramref name="repoRoot" />.
    /// </summary>
    /// <param name="repoRoot">Repository root to inspect (need not be normalized).</param>
    /// <param name="isolation">Isolation strategy stamped into the contract.</param>
    /// <param name="limits">Execution bounds stamped into the contract; defaults when null.</param>
    /// <param name="ct">Caller cancellation token (observed between git probes).</param>
    /// <returns>
    ///     The pinned contract on a clean HEAD; otherwise a failure naming the
    ///     cause (not a repo, git unavailable/timed out, or tracked dirt).
    /// </returns>
    public static async Task<Result<WorkspaceContract>> InspectAsync(
        string repoRoot,
        WorkspaceIsolation isolation = WorkspaceIsolation.Copy,
        WorkspaceLimits? limits = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Result.Failure<WorkspaceContract>("Workspace root must not be empty.");
        if (!Directory.Exists(repoRoot))
            return Result.Failure<WorkspaceContract>($"Workspace root '{repoRoot}' does not exist.");
        string root = Path.GetFullPath(repoRoot);

        Result<GitOutput> headRes = await RunGitAsync(root, "rev-parse HEAD", ct).ConfigureAwait(false);
        if (headRes.IsFailure)
            return Result.Failure<WorkspaceContract>(headRes.Error);
        if (headRes.Value.ExitCode != 0)
            return Result.Failure<WorkspaceContract>(
                $"Not a git repository at '{root}': git rev-parse HEAD failed: {Clip(headRes.Value.Stderr)}");
        string sha = headRes.Value.Stdout.Trim();
        if (sha.Length == 0)
            return Result.Failure<WorkspaceContract>(
                $"Not a git repository at '{root}': git rev-parse HEAD returned empty output.");

        Result<GitOutput> branchRes = await RunGitAsync(root, "branch --show-current", ct).ConfigureAwait(false);
        if (branchRes.IsFailure)
            return Result.Failure<WorkspaceContract>(branchRes.Error);
        if (branchRes.Value.ExitCode != 0)
            return Result.Failure<WorkspaceContract>(
                $"git branch --show-current failed in '{root}': {Clip(branchRes.Value.Stderr)}");
        string branchRaw = branchRes.Value.Stdout.Trim();
        string? branch = branchRaw.Length == 0 ? null : branchRaw;

        Result<GitOutput> statusRes = await RunGitAsync(root, "status --porcelain=v1", ct).ConfigureAwait(false);
        if (statusRes.IsFailure)
            return Result.Failure<WorkspaceContract>(statusRes.Error);
        if (statusRes.Value.ExitCode != 0)
            return Result.Failure<WorkspaceContract>(
                $"git status failed in '{root}': {Clip(statusRes.Value.Stderr)}");

        var tracked = new List<string>();
        bool untracked = false;
        foreach (string rawLine in statusRes.Value.Stdout.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;
            if (line.StartsWith("??", StringComparison.Ordinal))
            {
                untracked = true;
                continue;
            }
            if (line.StartsWith("!!", StringComparison.Ordinal))
                continue;
            string path = line.Length > 3 ? line[3..].Trim() : line;
            if (path.Length > 0)
                tracked.Add(path);
        }

        if (tracked.Count > 0)
            return Result.Failure<WorkspaceContract>(
                $"Dirty workspace at '{root}': {tracked.Count} tracked file(s) changed ({JoinDirt(tracked)}). " +
                "Commit, stash, or discard before starting isolated work — dirty state is rejected, never pinned.");

        return Result.Success(new WorkspaceContract(
            root,
            sha,
            branch,
            Array.Empty<string>(),
            untracked,
            isolation,
            limits ?? new WorkspaceLimits()));
    }

    private sealed record GitOutput(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    ///     Run one git probe in <paramref name="repoRoot" /> with a <see cref="GitTimeout" />
    ///     kill budget (the <c>ProcessDriver</c> exited-task-vs-infinite-delay shape:
    ///     kill the whole tree on timeout, never hang the caller).
    /// </summary>
    private static async Task<Result<GitOutput>> RunGitAsync(string repoRoot, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exited.TrySetResult(true);
        bool started;
        try
        {
            started = proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            || ex is FileNotFoundException
            || ex is InvalidOperationException
            || ex is UnauthorizedAccessException)
        {
            return Result.Failure<GitOutput>(
                $"Cannot start git {arguments} in '{repoRoot}': {ex.Message} Is git installed and on PATH?");
        }

        if (!started)
            return Result.Failure<GitOutput>($"Cannot start git {arguments} in '{repoRoot}': process failed to start.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = new CancellationTokenSource(GitTimeout);
        Task done = await Task.WhenAny(exited.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(false);
        if (!ReferenceEquals(done, exited.Task))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                // Best-effort kill: process already gone is the normal case.
                _ = ex;
            }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
            return Result.Failure<GitOutput>($"git {arguments} timed out after {GitTimeout.TotalSeconds:N0}s in '{repoRoot}' and was killed.");
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return Result.Success(new GitOutput(proc.ExitCode, stdout, stderr));
    }

    private static string JoinDirt(List<string> tracked)
    {
        int show = Math.Min(tracked.Count, MaxDirtPathsInError);
        var sb = new StringBuilder();
        for (int i = 0; i < show; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(tracked[i]);
        }
        if (tracked.Count > show)
            sb.Append(" (+").Append(tracked.Count - show).Append(" more)");
        return sb.ToString();
    }

    private static string Clip(string text)
    {
        string t = text.Trim();
        if (t.Length == 0)
            return "(no stderr)";
        return t.Length <= MaxStderrInError ? t : t[..MaxStderrInError] + "…";
    }
}
