using System.Globalization;
using System.Text;

namespace Harbor.Application.Sessions;

/// <summary>
///     Outcome of one <c>harbor run report</c> attempt (epic #42, slice S5
///     remainder, #379, driven end to end by S9, #397). The lifecycle states
///     are untouched by this enum: <c>Reported</c> is the only outcome that
///     moves the manifest (<c>Checked -&gt; Reported</c> through
///     <see cref="RunChangeTransitions" />); every other outcome leaves the
///     manifest where it was.
/// </summary>
public enum ReportOutcome
{
    /// <summary>The report was rendered from recorded artifacts; manifest is <c>Reported</c>.</summary>
    Reported,

    /// <summary>Fail-closed refusal (unknown run, missing or corrupt artifact, illegal state).</summary>
    Refused,
}

/// <summary>
///     One report attempt: its outcome, the process exit code, the
///     human-readable reason, and — on success — the plain-text rendering
///     (80 columns, no ANSI) the verb prints.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ExitCode">
///     Process exit code (<c>0</c> all declared checks passed,
///     <c>1</c> at least one failed, timed out, or never ran,
///     <c>2</c> the report cannot be produced).
/// </param>
/// <param name="Message">Printed summary (success) or the named refusal (failure).</param>
/// <param name="Text">Plain-text rendering on success; null on refusal.</param>
public sealed record ReportResult(ReportOutcome Outcome, int ExitCode, string Message, string? Text);

/// <summary>
///     Verification-report renderer (epic #42, stage S5): builds the
///     deliverable <see cref="ChangeReport" /> from recorded artifacts only —
///     the frozen change set (S3, #377) and the recorded checks (S4, #378) —
///     persists <c>report.json</c> plus <c>report.txt</c> beside the manifest,
///     and moves the manifest <c>Checked -&gt; Reported</c> through
///     <see cref="RunChangeTransitions" />.
/// </summary>
/// <remarks>
///     <para>
///         Fail-closed throughout: an unknown run, a missing or corrupt
///         artifact, an incoherent artifact set (run-id, base, or head
///         mismatch across the manifest, the frozen set, and the checks), or
///         a state other than <c>Checked</c> is exit 2 naming the defect —
///         never a half report, never a silent advance.
///     </para>
///     <para>
///         Idempotent re-report: reporting a run that is already
///         <c>Reported</c> rebuilds from the same artifacts and returns the
///         stored text when the rebuild is byte-identical. A rebuild that no
///         longer matches means the isolated copy changed after the report —
///         that is exit 2 naming the drift, and the stored
///         <c>report.json</c> stays the record. There is no auto-retry and no
///         blind resume.
///     </para>
/// </remarks>
public static class RunReporter
{
    /// <summary>Machine rendering beside the manifest (deterministic JSON).</summary>
    public const string ReportJsonFileName = "report.json";

    /// <summary>Plain-text rendering beside the manifest (80 columns, no ANSI).</summary>
    public const string ReportTextFileName = "report.txt";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Render the verification report for <paramref name="runIdValue" />.
    ///     At-most-once: a run already <c>Reported</c> re-reports idempotently,
    ///     anything else past <c>Checked</c> is refused with the named reason.
    /// </summary>
    /// <param name="runIdValue">Run id (validated like everywhere else: no path separators).</param>
    /// <param name="ct">Caller cancellation token.</param>
    public static async Task<ReportResult> ReportAsync(
        string? runIdValue,
        CancellationToken ct = default)
    {
        try
        {
            Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runIdValue);
            if (loaded.IsFailure)
                return Refused(loaded.Error);
            RunManifest manifest = loaded.Value;

            if (manifest.State == RunState.Reported)
                return await RereportAsync(manifest, ct).ConfigureAwait(false);

            Result<RunState> gate = RunChangeTransitions.TryTransition(manifest.State, RunState.Reported);
            if (gate.IsFailure)
                return Refused($"{gate.Error} Nothing was reported.");

            Result<BuiltReport> built = await BuildAsync(manifest, ct).ConfigureAwait(false);
            if (built.IsFailure)
                return Refused(built.Error);
            BuiltReport fresh = built.Value;

            Result<string> writtenJson = await WriteAtomicAsync(
                manifest.RunId, ReportJsonFileName, fresh.Json, ct).ConfigureAwait(false);
            if (writtenJson.IsFailure)
                return Refused(writtenJson.Error);
            Result<string> writtenText = await WriteAtomicAsync(
                manifest.RunId, ReportTextFileName, Encoding.UTF8.GetBytes(fresh.Text), ct).ConfigureAwait(false);
            if (writtenText.IsFailure)
                return Refused(writtenText.Error);

            Result<RunManifest> moved =
                WorkspaceMaterializer.TryAdvanceState(manifest.RunId, RunState.Reported);
            if (moved.IsFailure)
                return Refused(
                    $"{moved.Error} The report artifacts are on disk but the manifest did not advance — " +
                    $"re-run 'harbor run report {manifest.RunId}' after fixing the manifest.");
            return new ReportResult(
                ReportOutcome.Reported, fresh.Report.ExitCode, Summary(manifest.RunId, fresh.Report), fresh.Text);
        }
        catch (OperationCanceledException)
        {
            return Refused($"Report of run '{runIdValue}' was cancelled; nothing was reported.");
        }
    }

    private static async Task<ReportResult> RereportAsync(RunManifest manifest, CancellationToken ct)
    {
        string runDir = WorkspacePaths.Default.RunDir(manifest.RunId);
        string jsonPath = Path.Combine(runDir, ReportJsonFileName);
        string textPath = Path.Combine(runDir, ReportTextFileName);
        byte[] storedJson;
        string storedText;
        try
        {
            storedJson = await File.ReadAllBytesAsync(jsonPath, ct).ConfigureAwait(false);
            storedText = await File.ReadAllTextAsync(textPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return Refused(
                $"Run '{manifest.RunId}' is Reported but its report artifacts are missing " +
                $"('{jsonPath}', '{textPath}'): {ex.Message}");
        }

        Result<BuiltReport> rebuilt = await BuildAsync(manifest, ct).ConfigureAwait(false);
        if (rebuilt.IsFailure)
            return Refused(rebuilt.Error);
        BuiltReport fresh = rebuilt.Value;

        if (!BytesEqual(storedJson, fresh.Json))
            return Refused(
                $"Run '{manifest.RunId}' is Reported but a rebuild from its artifacts no longer matches " +
                $"the stored '{ReportJsonFileName}' — the isolated copy changed after the report. " +
                $"Refusing to present a second report; the stored '{jsonPath}' is the record.");
        return new ReportResult(
            ReportOutcome.Reported, fresh.Report.ExitCode, Summary(manifest.RunId, fresh.Report), storedText);
    }

    private sealed record BuiltReport(ChangeReport Report, byte[] Json, string Text);

    private static async Task<Result<BuiltReport>> BuildAsync(RunManifest manifest, CancellationToken ct)
    {
        Result<FrozenChangeSet> frozen = ChangeSetFreezer.TryLoad(manifest.RunId);
        if (frozen.IsFailure)
            return frozen.ConvertFailure<BuiltReport>();
        Result<CheckReport> checks = CheckRunner.TryLoad(manifest.RunId);
        if (checks.IsFailure)
            return checks.ConvertFailure<BuiltReport>();
        FrozenChangeSet frozenSet = frozen.Value;
        CheckReport checkReport = checks.Value;
        Result<string> coherent = CheckCoherence(manifest, frozenSet, checkReport);
        if (coherent.IsFailure)
            return coherent.ConvertFailure<BuiltReport>();

        Result<bool> dirty = await ProbeWorktreeDirtyAsync(manifest.WorktreePath, ct).ConfigureAwait(false);
        if (dirty.IsFailure)
            return dirty.ConvertFailure<BuiltReport>();
        bool isDirty = dirty.Value;

        ChangeReport report;
        try
        {
            report = ChangeReport.Create(
                manifest.RunId,
                manifest.BaseRevision,
                frozenSet.HeadRevision,
                ToChanged(frozenSet.Entries),
                ToRecorded(checkReport.Checks),
                frozenSet.IgnoredPathCount,
                manifest.TimeoutSeconds,
                manifest.MaxSteps,
                isDirty);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<BuiltReport>(
                $"Report of run '{manifest.RunId}' cannot be produced from its artifacts: {ex.Message}");
        }

        return Result.Success(new BuiltReport(report, report.ToJsonBytes(), report.ToPlainText()));
    }

    private static Result<string> CheckCoherence(
        RunManifest manifest, FrozenChangeSet frozen, CheckReport checks)
    {
        if (!frozen.RunId.Equals(manifest.RunId, StringComparison.Ordinal)
            || !checks.RunId.Equals(manifest.RunId, StringComparison.Ordinal))
            return Result.Failure<string>(
                $"Report of run '{manifest.RunId}' cannot be produced: artifact run ids do not match " +
                "the manifest — refusing to mix runs.");
        if (!frozen.BaseRevision.Equals(manifest.BaseRevision, StringComparison.Ordinal)
            || !checks.BaseRevision.Equals(manifest.BaseRevision, StringComparison.Ordinal))
            return Result.Failure<string>(
                $"Report of run '{manifest.RunId}' cannot be produced: the frozen set or the checks " +
                $"were recorded against a different base than the manifest ({manifest.BaseRevision}) — " +
                "refusing to report across bases.");
        if (!checks.HeadRevision.Equals(frozen.HeadRevision, StringComparison.Ordinal))
            return Result.Failure<string>(
                $"Report of run '{manifest.RunId}' cannot be produced: the checks ran on " +
                $"{checks.HeadRevision} but the frozen head is {frozen.HeadRevision} — " +
                "refusing to vouch across revisions.");
        return Result.Success(manifest.RunId);
    }

    private static async Task<Result<bool>> ProbeWorktreeDirtyAsync(string worktreePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return Result.Success(false);
        return (await WorkspaceMaterializer.RunGitAsync(
            worktreePath, ["status", "--porcelain=v1"], ProbeTimeout, ct).ConfigureAwait(false))
            .Bind(status => status.ExitCode != 0
                ? Result.Failure<bool>(
                    $"Cannot inspect worktree at '{worktreePath}': " +
                    $"{WorkspaceMaterializer.Clip(status.Stderr)}")
                : Result.Success(status.Stdout.Trim().Length > 0));
    }

    private static IReadOnlyList<ChangedPathEntry> ToChanged(IReadOnlyList<FrozenChangeEntry> entries)
    {
        var changed = new List<ChangedPathEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
            changed.Add(ChangedPathEntry.Create(entries[i].Path, entries[i].Status));
        return changed;
    }

    private static IReadOnlyList<RecordedCheck> ToRecorded(IReadOnlyList<CheckResult> results)
    {
        var recorded = new List<RecordedCheck>(results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            CheckResult r = results[i];
            if (r.Outcome == CheckOutcome.Passed && r.ExitCode == 0)
                recorded.Add(RecordedCheck.Passed(r.Name, r.CommandLine, r.HeadRevision, r.EnvKeys));
            else if (r.Outcome == CheckOutcome.Failed && r.ExitCode.HasValue && r.ExitCode.Value != 0)
                recorded.Add(RecordedCheck.Failed(r.Name, r.CommandLine, r.ExitCode.Value, r.HeadRevision, r.EnvKeys));
            else
                recorded.Add(RecordedCheck.DidNotRun(
                    r.Name, r.CommandLine, MissReason(r), r.HeadRevision, r.EnvKeys,
                    r.Outcome == CheckOutcome.TimedOut));
        }
        return recorded;
    }

    private static string MissReason(CheckResult r)
    {
        if (r.Outcome == CheckOutcome.TimedOut)
            return "timed out; no result was recorded";
        if (r.Outcome == CheckOutcome.SpawnFailed)
        {
            string detail = r.Output.Trim();
            return detail.Length == 0 ? "spawn failed; no result was recorded" : detail;
        }
        if (r.Outcome == CheckOutcome.Cancelled)
            return "cancelled; no result was recorded";
        if (r.Outcome == CheckOutcome.Skipped)
            return "skipped: an earlier check stopped the run";
        return "did not run; no result was recorded";
    }

    private static string Summary(string runId, ChangeReport report) =>
        $"Run '{runId}': reported (" +
        $"{report.Changed.Count.ToString(CultureInfo.InvariantCulture)} changed, " +
        $"{report.Verified.Count.ToString(CultureInfo.InvariantCulture)} verified, " +
        $"{report.NotVerified.Count.ToString(CultureInfo.InvariantCulture)} not verified, " +
        $"exit {report.ExitCode.ToString(CultureInfo.InvariantCulture)}). State Reported.";

    private static ReportResult Refused(string error) =>
        new(ReportOutcome.Refused, ChangeReport.ExitCannotProduce, error, null);

    private static async Task<Result<string>> WriteAtomicAsync(
        string runId, string fileName, byte[] bytes, CancellationToken ct)
    {
        string runDir = WorkspacePaths.Default.RunDir(runId);
        try
        {
            Directory.CreateDirectory(runDir);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Result.Failure<string>($"Cannot create run directory '{runDir}': {ex.Message}");
        }
        string path = Path.Combine(runDir, fileName);
        string tmp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            return Result.Failure<string>($"Cannot write report for run '{runId}': {ex.Message}");
        }
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            DeleteQuiet(tmp);
            return Result.Failure<string>($"Cannot write report for run '{runId}': {ex.Message}");
        }
        return Result.Success(path);
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }
}
