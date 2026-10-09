using System.Diagnostics;
using System.Text;

namespace Harbor.Application.Sessions;

/// <summary>
///     Outcome of one <c>harbor run accept</c> attempt (epic #42, slice S6,
///     #382). The lifecycle states are untouched by this enum: <c>Applied</c>
///     is the only outcome that moves the manifest (<c>Reported -&gt; Accepted</c>
///     through <see cref="RunChangeTransitions" />); every other outcome
///     leaves the manifest where it was. In particular <c>NeedsManualRepair</c>
///     is an <i>accept outcome</i> recorded in <c>accept.log</c>, not a run
///     lifecycle state — the S9 map stays the single authority and gains no
///     new axis for a half-applied tree.
/// </summary>
public enum AcceptOutcome
{
    /// <summary>The patch landed as uncommitted staged changes; manifest is <c>Accepted</c>.</summary>
    Applied,

    /// <summary>Base moved or tree dirty — nothing written, exit 3.</summary>
    Conflict,

    /// <summary><c>--dry-run</c>: printed, nothing written, exit 0.</summary>
    Cancelled,

    /// <summary>Any other fail-closed refusal (not Reported, no patch, re-verify unavailable, apply failed with clean rollback).</summary>
    Refused,

    /// <summary>Apply failed and the rollback did not restore a clean tree (or the manifest could not record success) — recovery commands printed, exit 4.</summary>
    NeedsManualRepair,
}

/// <summary>
///     One accept attempt: its outcome, the process exit code, and the
///     human-readable reason (named revisions, dirty paths, or recovery
///     commands — never a bare refusal).
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ExitCode">Process exit code (0 applied/cancelled, 3 conflict, 2 bad usage, 4 named-stage failure).</param>
/// <param name="Message">Printed to stdout on success, stderr otherwise.</param>
public sealed record AcceptResult(AcceptOutcome Outcome, int ExitCode, string Message);

/// <summary>
///     Change applier (epic #42, stage S6): the one step that writes to the
///     operator's tree, so it is the most conservative step in the epic.
///     Fail-closed on every path: the base-unchanged gate runs before any
///     write, the apply sequence never uses <c>--3way</c>, no commit / branch /
///     stash / checkout is ever created, and the patch path is always derived
///     from the run manifest — a <c>RunId</c> can never point the applier at
///     an arbitrary file.
/// </summary>
/// <remarks>
///     <para>
///         Apply sequence: <c>git apply --check</c> (gate) → re-verify HEAD is
///         still the pinned revision → <c>git apply --index</c>. If apply fails
///         after the check passed, <c>git apply --reverse</c> is attempted only
///         because the pre-flight verified a clean tree; if the rollback itself
///         fails the outcome is <see cref="AcceptOutcome.NeedsManualRepair" />
///         with the exact recovery commands — never a false success.
///     </para>
///     <para>
///         Honest limits of this slice: the frozen <c>change.patch</c> is
///         produced by S3 (#377), the check suite by S4 (#378) and the report by
///         S5 (#379) — none landed yet. A run without <c>change.patch</c> is
///         refused naming S3, and <c>--reverify</c> is refused naming S4/S5.
///         Nothing is faked past the gate.
///     </para>
/// </remarks>
public static class ChangeApplier
{
    /// <summary>Frozen change set beside the manifest (produced by S3).</summary>
    public const string ChangePatchFileName = "change.patch";

    /// <summary>Append-only audit trail beside the manifest: one line per attempt.</summary>
    public const string AcceptLogFileName = "accept.log";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(60);
    private const int MaxStderrInError = 500;
    private const int MaxDirtPathsInError = 20;
    private const int MaxLogDetailChars = 300;

    /// <summary>
    ///     Attempt to accept <paramref name="runIdValue" /> into the operator's
    ///     tree. At-most-once: a run already <c>Accepted</c> is refused with
    ///     exit 4 and nothing changes.
    /// </summary>
    /// <param name="runIdValue">Run id (validated like everywhere else: no path separators).</param>
    /// <param name="dryRun">Print the exact git commands and <c>git diff --stat</c>; write nothing.</param>
    /// <param name="reverify">Re-run the S4 suite before applying (refused until S4/S5 land).</param>
    /// <param name="ct">Caller cancellation token.</param>
    public static async Task<AcceptResult> AcceptAsync(
        string? runIdValue,
        bool dryRun = false,
        bool reverify = false,
        CancellationToken ct = default)
    {
        if (dryRun && reverify)
            return new AcceptResult(
                AcceptOutcome.Refused, 2,
                "Cannot combine --dry-run with --reverify: dry-run prints without applying, " +
                "re-verify applies only when the checks pass.");

        Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runIdValue);
        if (loaded.IsFailure)
            return new AcceptResult(AcceptOutcome.Refused, 2, loaded.Error);
        RunManifest manifest = loaded.Value;

        AcceptResult result;
        try
        {
            result = await AcceptCoreAsync(manifest, dryRun, reverify, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new AcceptResult(
                AcceptOutcome.Refused, 4,
                $"Accept of run '{manifest.RunId}' was cancelled; nothing was committed by the applier — " +
                $"verify with: git -C \"{manifest.RepoRoot}\" status --porcelain");
        }

        string? logWarning = await AppendAcceptLogAsync(manifest, dryRun, reverify, result).ConfigureAwait(false);
        if (logWarning is not null)
            result = result with { Message = result.Message + " " + logWarning };
        return result;
    }

    private static async Task<AcceptResult> AcceptCoreAsync(
        RunManifest manifest, bool dryRun, bool reverify, CancellationToken ct)
    {
        Result<RunState> gate = RunChangeTransitions.TryTransition(manifest.State, RunState.Accepted);
        if (gate.IsFailure)
            return dryRun
                ? Cancelled($"Run '{manifest.RunId}' is {manifest.State}: dry-run only — " +
                    $"accept would refuse: {gate.Error} Nothing written.")
                : new AcceptResult(AcceptOutcome.Refused, 4, $"{gate.Error} Nothing written.");

        if (reverify)
            return new AcceptResult(
                AcceptOutcome.Refused, 4,
                $"Stage 'reverify' needs the S4 check suite (#378) and the S5 report (#379): " +
                "neither is implemented — refusing to apply without re-verification. Nothing written.");

        string patchPath = Path.Combine(WorkspaceMaterializer.RunDir(manifest.RunId), ChangePatchFileName);
        if (!File.Exists(patchPath))
            return dryRun
                ? Cancelled($"Run '{manifest.RunId}' has no {ChangePatchFileName} yet: " +
                    "stage 'freeze' (S3, #377) is not implemented. Nothing written.")
                : new AcceptResult(
                    AcceptOutcome.Refused, 4,
                    $"Run '{manifest.RunId}' has no {ChangePatchFileName}: " +
                    "stage 'freeze' (S3, #377) has not produced it — refusing to invent a change set. Nothing written.");

        Result<string> head = await RevParseHeadAsync(manifest.RepoRoot, ct).ConfigureAwait(false);
        if (head.IsFailure)
            return dryRun
                ? Cancelled($"Run '{manifest.RunId}': cannot verify base in '{manifest.RepoRoot}': " +
                    $"{head.Error} Nothing written.")
                : new AcceptResult(
                    AcceptOutcome.Refused, 4,
                    $"Cannot verify base revision in '{manifest.RepoRoot}': {head.Error} Nothing written.");
        string actualHead = head.Value;

        Result<IReadOnlyList<string>> dirt = await TrackedDirtAsync(manifest.RepoRoot, ct).ConfigureAwait(false);
        if (dirt.IsFailure)
            return dryRun
                ? Cancelled($"Run '{manifest.RunId}': cannot inspect tree in '{manifest.RepoRoot}': " +
                    $"{dirt.Error} Nothing written.")
                : new AcceptResult(
                    AcceptOutcome.Refused, 4,
                    $"Cannot inspect operator tree in '{manifest.RepoRoot}': {dirt.Error} Nothing written.");

        if (!actualHead.Equals(manifest.BaseRevision, StringComparison.Ordinal))
        {
            string reason =
                $"base moved {manifest.BaseRevision}->{actualHead}: the contract was pinned " +
                $"against {manifest.BaseRevision} but the operator's tree is at {actualHead} — " +
                "refusing to apply a change nobody verified against this base. Nothing written.";
            return dryRun
                ? Cancelled($"Run '{manifest.RunId}': dry-run only — accept would refuse: {reason}")
                : new AcceptResult(AcceptOutcome.Conflict, 3, $"Run '{manifest.RunId}': {reason}");
        }

        if (dirt.Value.Count > 0)
        {
            string reason =
                $"operator tree is dirty ({dirt.Value.Count} tracked file(s): {JoinPaths(dirt.Value)}) — " +
                "commit, stash, or discard before accepting. Nothing written.";
            return dryRun
                ? Cancelled($"Run '{manifest.RunId}': dry-run only — accept would refuse: {reason}")
                : new AcceptResult(AcceptOutcome.Conflict, 3, $"Run '{manifest.RunId}': {reason}");
        }

        if (dryRun)
            return await DryRunAsync(manifest, patchPath, actualHead, ct).ConfigureAwait(false);

        Result<string> check = await RunGitAsync(
            manifest.RepoRoot, ["apply", "--check", patchPath], ApplyTimeout, ct).ConfigureAwait(false);
        if (check.IsFailure)
            return Fail($"Cannot run git apply --check for run '{manifest.RunId}': {check.Error} Nothing written.");
        if (check.Value.ExitCode != 0)
            return Fail($"Run '{manifest.RunId}': git apply --check failed: " +
                $"{Clip(check.Value.Stderr)} Nothing written, tree unchanged.");

        Result<string> recheck = await RevParseHeadAsync(manifest.RepoRoot, ct).ConfigureAwait(false);
        if (recheck.IsFailure)
            return Fail($"Run '{manifest.RunId}': cannot re-verify base after --check: " +
                $"{recheck.Error} Nothing written.");
        if (!recheck.Value.Equals(manifest.BaseRevision, StringComparison.Ordinal))
            return new AcceptResult(
                AcceptOutcome.Conflict, 3,
                $"Run '{manifest.RunId}': base moved {manifest.BaseRevision}->{recheck.Value} " +
                "between --check and apply — refusing a racy apply. Nothing written.");

        Result<string> apply = await RunGitAsync(
            manifest.RepoRoot, ["apply", "--index", patchPath], ApplyTimeout, ct).ConfigureAwait(false);
        if (apply.IsFailure)
            return Fail($"Cannot run git apply for run '{manifest.RunId}': {apply.Error} " +
                "The check passed but the apply did not run — verify with: " +
                $"git -C \"{manifest.RepoRoot}\" status --porcelain");
        if (apply.Value.ExitCode != 0)
            return await RollbackAsync(manifest, patchPath, apply.Value.Stderr, ct).ConfigureAwait(false);

        Result<RunManifest> advanced = WorkspaceMaterializer.TryAdvanceState(manifest.RunId, RunState.Accepted);
        if (advanced.IsFailure)
            return new AcceptResult(
                AcceptOutcome.NeedsManualRepair, 4,
                $"Run '{manifest.RunId}': the patch IS applied (staged, uncommitted) but the manifest " +
                $"could not record it: {advanced.Error} Recovery: inspect with " +
                $"git -C \"{manifest.RepoRoot}\" status --porcelain and " +
                $"git -C \"{manifest.RepoRoot}\" diff --cached --stat; to undo the apply run " +
                $"git -C \"{manifest.RepoRoot}\" apply --reverse --index \"{patchPath}\".");

        return new AcceptResult(
            AcceptOutcome.Applied, 0,
            $"Run '{manifest.RunId}': applied as uncommitted staged changes in '{manifest.RepoRoot}' " +
            $"(base {manifest.BaseRevision}). No commit created — review with " +
            $"git -C \"{manifest.RepoRoot}\" diff --cached --stat.");

        static AcceptResult Fail(string message) => new(AcceptOutcome.Refused, 4, message);
        static AcceptResult Cancelled(string message) => new(AcceptOutcome.Cancelled, 0, message);
    }

    private static async Task<AcceptResult> DryRunAsync(
        RunManifest manifest, string patchPath, string head, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"Run '{manifest.RunId}': dry-run — would apply \"{patchPath}\" ");
        sb.Append($"at base {head} with no --3way, no commit, no branch, no stash, no checkout. Commands:");
        sb.Append($" git -C \"{manifest.RepoRoot}\" rev-parse HEAD;");
        sb.Append($" git -C \"{manifest.RepoRoot}\" status --porcelain=v1;");
        sb.Append($" git -C \"{manifest.RepoRoot}\" apply --check \"{patchPath}\";");
        sb.Append($" git -C \"{manifest.RepoRoot}\" apply --index \"{patchPath}\".");

        Result<string> check = await RunGitAsync(
            manifest.RepoRoot, ["apply", "--check", patchPath], ApplyTimeout, ct).ConfigureAwait(false);
        if (check.IsFailure)
            sb.Append($" Pre-check did not run: {check.Error}");
        else if (check.Value.ExitCode != 0)
            sb.Append($" Pre-check FAILS: {Clip(check.Value.Stderr)}");
        else
            sb.Append(" Pre-check passes.");

        Result<string> stat = await RunGitAsync(
            manifest.RepoRoot, ["diff", "--stat"], ProbeTimeout, ct).ConfigureAwait(false);
        if (stat.IsFailure)
            sb.Append(" git diff --stat did not run.");
        else if (stat.Value.Stdout.Trim().Length == 0)
            sb.Append(" git diff --stat: clean (no uncommitted changes).");
        else
            sb.Append($" git diff --stat: {OneLine(stat.Value.Stdout, 300)}");

        sb.Append(" Nothing written.");
        return new AcceptResult(AcceptOutcome.Cancelled, 0, sb.ToString());
    }

    private static async Task<AcceptResult> RollbackAsync(
        RunManifest manifest, string patchPath, string applyStderr, CancellationToken ct)
    {
        // The pre-flight verified a clean tree, so --reverse can only undo what
        // this attempt staged — it is safe to attempt. If it fails, or the tree
        // is still dirty afterwards, the operator gets exact recovery commands.
        Result<string> reverse = await RunGitAsync(
            manifest.RepoRoot, ["apply", "--reverse", "--index", patchPath], ApplyTimeout, ct).ConfigureAwait(false);
        Result<IReadOnlyList<string>> dirt = await TrackedDirtAsync(manifest.RepoRoot, ct).ConfigureAwait(false);
        bool clean = reverse.IsSuccess
            && reverse.Value.ExitCode == 0
            && dirt.IsSuccess
            && dirt.Value.Count == 0;
        if (clean)
            return new AcceptResult(
                AcceptOutcome.Refused, 4,
                $"Run '{manifest.RunId}': git apply failed ({Clip(applyStderr)}); " +
                "rollback (apply --reverse) restored a clean tree. Nothing applied.");

        var sb = new StringBuilder();
        sb.Append($"Run '{manifest.RunId}': git apply failed ({Clip(applyStderr)}); ");
        sb.Append("rollback did NOT restore a clean tree — manual repair required. Recovery: ");
        sb.Append($"git -C \"{manifest.RepoRoot}\" status --porcelain; ");
        sb.Append($"git -C \"{manifest.RepoRoot}\" apply --reverse --index \"{patchPath}\"; ");
        sb.Append($"verify with git -C \"{manifest.RepoRoot}\" status --porcelain (must be empty) ");
        sb.Append("before retrying or releasing the run.");
        return new AcceptResult(AcceptOutcome.NeedsManualRepair, 4, sb.ToString());
    }

    private static async Task<Result<string>> RevParseHeadAsync(string repoRoot, CancellationToken ct)
    {
        Result<GitOutput> head = await RunGitAsync(
            repoRoot, ["rev-parse", "HEAD"], ProbeTimeout, ct).ConfigureAwait(false);
        if (head.IsFailure)
            return head.ConvertFailure<string>();
        if (head.Value.ExitCode != 0)
            return Result.Failure<string>(
                $"git rev-parse HEAD failed in '{repoRoot}': {Clip(head.Value.Stderr)}");
        string sha = head.Value.Stdout.Trim();
        if (sha.Length == 0)
            return Result.Failure<string>(
                $"git rev-parse HEAD returned empty output in '{repoRoot}'.");
        return Result.Success(sha);
    }

    private static async Task<Result<IReadOnlyList<string>>> TrackedDirtAsync(string repoRoot, CancellationToken ct)
    {
        Result<GitOutput> status = await RunGitAsync(
            repoRoot, ["status", "--porcelain=v1"], ProbeTimeout, ct).ConfigureAwait(false);
        if (status.IsFailure)
            return status.ConvertFailure<IReadOnlyList<string>>();
        if (status.Value.ExitCode != 0)
            return Result.Failure<IReadOnlyList<string>>(
                $"git status failed in '{repoRoot}': {Clip(status.Value.Stderr)}");
        var tracked = new List<string>();
        foreach (string rawLine in status.Value.Stdout.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;
            if (line.StartsWith("??", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("!!", StringComparison.Ordinal))
                continue;
            string path = line.Length > 3 ? line[3..].Trim() : line;
            if (path.Length > 0)
                tracked.Add(path);
        }
        return Result.Success<IReadOnlyList<string>>(tracked);
    }

    private static async Task<string?> AppendAcceptLogAsync(
        RunManifest manifest, bool dryRun, bool reverify, AcceptResult result)
    {
        string line =
            $"{DateTimeOffset.UtcNow:O} run={manifest.RunId} " +
            $"dry_run={(dryRun ? 1 : 0)} reverify={(reverify ? 1 : 0)} " +
            $"outcome={result.Outcome} exit={result.ExitCode} " +
            $"{OneLine(result.Message, MaxLogDetailChars)}";
        try
        {
            string logPath = Path.Combine(
                WorkspaceMaterializer.RunDir(manifest.RunId), AcceptLogFileName);
            await File.AppendAllTextAsync(logPath, line + "\n").ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            return $"(accept.log append failed: {OneLine(ex.Message, 120)})";
        }
    }

    private static string JoinPaths(IReadOnlyList<string> paths)
    {
        int show = Math.Min(paths.Count, MaxDirtPathsInError);
        var sb = new StringBuilder();
        for (int i = 0; i < show; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(paths[i]);
        }
        if (paths.Count > show)
            sb.Append(" (+").Append(paths.Count - show).Append(" more)");
        return sb.ToString();
    }

    private sealed record GitOutput(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    ///     One git invocation via argument list (never a shell string), mirroring
    ///     the S1/S2 probe helpers: non-interactive environment, kill budget.
    /// </summary>
    private static async Task<Result<GitOutput>> RunGitAsync(
        string workingDirectory, string[] argv, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in argv)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

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
                $"Cannot start git {argv[0]} in '{workingDirectory}': {ex.Message} Is git installed and on PATH?");
        }
        if (!started)
            return Result.Failure<GitOutput>($"Cannot start git {argv[0]} in '{workingDirectory}': process failed to start.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = new CancellationTokenSource(timeout);
        Task done = await Task.WhenAny(exited.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);
        if (!ReferenceEquals(done, exited.Task))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
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
            return Result.Failure<GitOutput>(
                $"git {argv[0]} timed out after {timeout.TotalSeconds:N0}s in '{workingDirectory}' and was killed.");
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return Result.Success(new GitOutput(proc.ExitCode, stdout, stderr));
    }

    private static string Clip(string text)
    {
        string t = text.Trim();
        if (t.Length == 0)
            return "(no stderr)";
        return t.Length <= MaxStderrInError ? t : t[..MaxStderrInError] + "…";
    }

    private static string OneLine(string text, int max)
    {
        var sb = new StringBuilder();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append(" | ");
            sb.Append(line);
            if (sb.Length >= max)
                break;
        }
        string flat = sb.ToString().Trim();
        if (flat.Length == 0)
            return "(empty)";
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
