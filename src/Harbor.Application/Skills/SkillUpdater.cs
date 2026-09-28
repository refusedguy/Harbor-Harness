using System.Diagnostics;
using CSharpFunctionalExtensions;

namespace Harbor.Application.Skills;

// KILLER_FEATURES §2.7 Feature 10 (issue #384): the "clicking opens an update
// dialog" half of the Orca pill — `/skills update [name…]` re-resolves the
// stale skills from their source instead of leaving the user to fix a
// `● changed` pill by hand.
//
// The updater is BCL-only and Application-layer (same rule as
// `SkillFreshnessSeeder`): it takes plain skill names, never the UI's
// `SkillFreshnessEntry`, and returns a report the composition root maps onto
// renderer output. A skills source is "re-resolvable" when the skills root
// lives inside a git work tree — then `git pull --ff-only` is the reinstall.
// Anything else (plain directory, no repo, git missing, network down) is a
// no-op or a reported failure, never a throw: a stale skill is a nuisance, not
// a reason to take the REPL down.

/// <summary>How a <c>/skills update</c> attempt ended (issue #384).</summary>
public enum SkillUpdateOutcome
{
    /// <summary>The source was re-resolved (git pull succeeded).</summary>
    Updated,

    /// <summary>Nothing was attempted: no source, not git-backed, or nothing stale.</summary>
    NoOp,

    /// <summary>The attempt was made and failed — freshness state stays untouched.</summary>
    Failed,
}

/// <summary>Outcome of one <c>/skills update</c> attempt (issue #384).</summary>
/// <param name="Outcome">Updated / NoOp / Failed.</param>
/// <param name="Targeted">How many skills the attempt targeted (0 when source-wide).</param>
/// <param name="Message">User-facing one-liner; never empty.</param>
public sealed record SkillUpdateReport(SkillUpdateOutcome Outcome, int Targeted, string Message)
{
    /// <summary>Report for a re-resolved source.</summary>
    public static SkillUpdateReport Updated(int targeted, string message) =>
        new(SkillUpdateOutcome.Updated, targeted, message);

    /// <summary>Report for an intentional no-op (nothing stale / not git-backed).</summary>
    public static SkillUpdateReport NoOp(string message) => new(SkillUpdateOutcome.NoOp, 0, message);

    /// <summary>Report for a failed attempt; the caller must not reseed as if it worked.</summary>
    public static SkillUpdateReport Failed(string message) => new(SkillUpdateOutcome.Failed, 0, message);
}

/// <summary>Exit shape of one git invocation (mirrors <c>WorkspaceInspector</c>).</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
public sealed record SkillGitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
///     Git seam: runs one git command in <paramref name="workingDirectory" />.
///     Tests inject a fake so the update path never shells out in CI.
/// </summary>
public delegate Task<Result<SkillGitResult>> SkillGitRunner(
    string workingDirectory,
    IReadOnlyList<string> arguments,
    CancellationToken ct);

/// <summary>
///     Re-resolves stale skills from their source (KILLER_FEATURES §2.7
///     Feature 10, issue #384). Best-effort and total: every path returns a
///     <see cref="SkillUpdateReport" /> and nothing escapes as an exception.
/// </summary>
public static class SkillUpdater
{
    /// <summary>Kill budget for a single git invocation (never hang the REPL).</summary>
    internal static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Human-readable form of the re-resolve command, for report messages.</summary>
    internal const string PullDescription = "git pull --ff-only";

    private static readonly string[] PullArguments = ["pull", "--ff-only"];

    /// <summary>
    ///     Re-resolve <paramref name="names" /> (empty/null = the whole source)
    ///     from the git work tree that owns the skills root. Project root
    ///     (<c>.harbor/skills</c>) wins over the global one
    ///     (<c>~/.harbor/skills</c>), mirroring
    ///     <see cref="SkillFreshnessSeeder" />'s shadowing rule.
    /// </summary>
    /// <param name="names">Requested skill names; empty means "everything in the source".</param>
    /// <param name="projectSkillsRoot">Project skills root, or null when absent.</param>
    /// <param name="globalSkillsRoot">Global skills root, or null when absent.</param>
    /// <param name="git">Git seam; null uses the real <c>git</c> binary.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<SkillUpdateReport> UpdateAsync(
        IReadOnlyList<string>? names,
        string? projectSkillsRoot,
        string? globalSkillsRoot,
        SkillGitRunner? git = null,
        CancellationToken ct = default)
    {
        try
        {
            string? root = ResolveSourceRoot(names, projectSkillsRoot, globalSkillsRoot);
            if (root is null)
            {
                return SkillUpdateReport.NoOp(
                    "no skills source found (.harbor/skills or ~/.harbor/skills) — install skills first.");
            }

            string? repo = FindRepositoryRoot(root);
            if (repo is null)
            {
                return SkillUpdateReport.NoOp(
                    $"skills source '{root}' is not git-backed — update it by hand, then run /skills refresh.");
            }

            SkillGitRunner run = git ?? RunGitAsync;
            var result = await run(repo, PullArguments, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return SkillUpdateReport.Failed(result.Error);
            }

            var output = result.Value;
            if (output.ExitCode != 0)
            {
                return SkillUpdateReport.Failed(
                    $"git pull failed in '{root}' (exit {output.ExitCode}): {FirstLine(output.Stderr)}");
            }

            int targeted = names?.Count ?? 0;
            return SkillUpdateReport.Updated(
                targeted,
                targeted > 0
                    ? $"re-resolved {targeted} skill(s) from '{root}' ({PullDescription})."
                    : $"re-resolved skills from '{root}' ({PullDescription}).");
        }
        catch (OperationCanceledException)
        {
            return SkillUpdateReport.Failed("skill update cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return SkillUpdateReport.Failed(ex.Message);
        }
    }

    /// <summary>
    ///     Pick the skills root to re-resolve: the first existing candidate
    ///     (project before global) that owns a requested skill; with no request
    ///     the first existing one. Null only when neither root exists. A
    ///     requested-but-absent skill still resolves to the first existing root
    ///     — pulling the source is exactly how a <c>✗ missing</c> skill
    ///     arrives.
    /// </summary>
    private static string? ResolveSourceRoot(
        IReadOnlyList<string>? names,
        string? projectSkillsRoot,
        string? globalSkillsRoot)
    {
        string? first = null;
        foreach (string? candidate in new[] { projectSkillsRoot, globalSkillsRoot })
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            first ??= candidate;
            if (names is null || names.Count == 0)
            {
                return candidate;
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (Owns(candidate, names[i]!))
                {
                    return candidate;
                }
            }
        }

        return first;
    }

    /// <summary>True when <paramref name="root" /> holds <paramref name="name" /> as a file or directory.</summary>
    private static bool Owns(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string path = Path.Combine(root, name);
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Nearest ancestor of <paramref name="directory" /> (inclusive) that
    ///     contains a <c>.git</c> entry, or null when the source is not a git
    ///     work tree. Pure directory probing — no process spawn.
    /// </summary>
    public static string? FindRepositoryRoot(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(directory);
            for (var probe = dir; probe is not null; probe = probe.Parent)
            {
                if (Directory.Exists(Path.Combine(probe.FullName, ".git"))
                    || File.Exists(Path.Combine(probe.FullName, ".git")))
                {
                    return probe.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    ///     Real git runner — <c>WorkspaceInspector.RunGitAsync</c> shape: kill
    ///     the whole tree on the <see cref="GitTimeout" /> budget, redirect both
    ///     streams, never throw for a missing binary.
    /// </summary>
    private static async Task<Result<SkillGitResult>> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exited.TrySetResult(true);

        bool started;
        try
        {
            started = proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or FileNotFoundException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            return Result.Failure<SkillGitResult>(
                $"cannot start git {string.Join(' ', arguments)} in '{workingDirectory}': {ex.Message}");
        }

        if (!started)
        {
            return Result.Failure<SkillGitResult>($"git {string.Join(' ', arguments)} failed to start.");
        }

        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = new CancellationTokenSource(GitTimeout);
        Task done = await Task.WhenAny(exited.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token))
            .ConfigureAwait(false);
        if (!ReferenceEquals(done, exited.Task))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited — the normal race.
            }

            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Drain best-effort: the timeout report below is the useful one.
                _ = ex;
            }

            return Result.Failure<SkillGitResult>(
                $"git {string.Join(' ', arguments)} timed out after {GitTimeout.TotalSeconds:N0}s in '{workingDirectory}'.");
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return Result.Success(new SkillGitResult(
            proc.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false)));
    }

    /// <summary>First non-empty line of git's stderr, trimmed for a one-line report.</summary>
    private static string FirstLine(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "no error output";
        }

        foreach (string line in stderr.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return stderr.Trim();
    }
}
