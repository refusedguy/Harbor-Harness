namespace Harbor.Evals;

/// <summary>evals v0 entry: --tasks <dir> --profile <json> [--batch <id>] [--attempts N].</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string tasksDir = Arg(args, "--tasks") ?? "evals/tasks";
        string profilePath = Arg(args, "--profile") ?? "evals/profiles/local.json";
        string batchId = Arg(args, "--batch") ?? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        int attempts = int.TryParse(Arg(args, "--attempts"), out int n) ? Math.Max(1, n) : 1;

        string repoRoot = FindRepoRoot();
        var profile = EvalProfile.Load(Path.GetFullPath(profilePath), repoRoot);
        string resultsRoot = Path.GetFullPath("evals/results");
        string batchDir = Path.Combine(resultsRoot, batchId);
        Directory.CreateDirectory(batchDir);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var summaries = new List<SummaryBuilder.AttemptSummary>();
        foreach (string taskDir in Directory.GetDirectories(Path.GetFullPath(tasksDir)).OrderBy(Path.GetFileName))
        {
            EvalTask task;
            try
            {
                task = TaskLoader.Load(taskDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SKIP {taskDir}: {ex.Message}");
                continue;
            }

            for (int i = 1; i <= attempts; i++)
            {
                string attemptRoot = Path.Combine(batchDir, task.Id, $"attempt-{i}");
                Directory.CreateDirectory(attemptRoot);
                try
                {
                    summaries.Add(await RunAttemptAsync(profile, task, batchId, i, attemptRoot, cts.Token));
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Cancelled.");
                    SummaryBuilder.Write(resultsRoot, batchId, summaries);
                    return 130;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"HARNESS-ERROR {task.Id} attempt {i}: {ex.Message}");
                    summaries.Add(new SummaryBuilder.AttemptSummary(task.Id, i, "inconclusive", "not_evaluated", "harness_error", "harness_or_crash", 0));
                }
            }
        }

        SummaryBuilder.Write(resultsRoot, batchId, summaries);
        Console.WriteLine($"Done: {batchDir}");
        return 0;
    }

    private static async Task<SummaryBuilder.AttemptSummary> RunAttemptAsync(
        EvalProfile profile, EvalTask task, string batchId, int attempt, string attemptRoot, CancellationToken ct)
    {
        Console.WriteLine($"== {task.Id} attempt {attempt}");
        var prep = WorkspacePreparer.Prepare(attemptRoot, task);

        var drive = await ProcessDriver.RunAsync(profile, task.Prompt, prep.WorkspaceDir, task.TimeoutSeconds, ct);
        var events = EventParser.Parse(drive.Stdout);
        var after = WorkspacePreparer.FileManifest.Capture(prep.WorkspaceDir);

        string verificationOut = Path.Combine(attemptRoot, "verification-output.json");
        var verify = await VerifierRunner.RunAsync(task, prep.VerifierDir, prep.WorkspaceDir,
            verificationOut, task.VerifierTimeoutSeconds, ct);

        var verdict = ArtifactWriter.Decide(drive, verify, prep.Initial, after, task);
        ArtifactWriter.WriteAll(attemptRoot, task, profile, drive, events, prep.Initial, after,
            verify, verdict, batchId, attempt);

        Console.WriteLine($"   {verdict.TaskOutcome} (exec={verdict.Execution}, verify={verdict.Verification}, constraints={verdict.ConstraintsOutcome})");
        return new SummaryBuilder.AttemptSummary(task.Id, attempt, verdict.TaskOutcome,
            verdict.ConstraintsOutcome, verdict.Execution, verdict.PrimaryFailure,
            (drive.EndedUtc - drive.StartedUtc).TotalSeconds);
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string FindRepoRoot()
    {
        string? dir = Path.GetDirectoryName(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Harbor.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }
}
