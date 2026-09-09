using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harbor.Evals;

/// <summary>Writes attempt.json, manifests, patch, verification + verdict.</summary>
internal static class ArtifactWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed record AttemptVerdict(
        string Execution, // completed | timed_out | crashed | harness_error
        string Verification, // pass | fail | error
        string TaskOutcome, // solved | failed | inconclusive
        string ConstraintsOutcome, // pass | fail | not_evaluated
        string? AgentClaim, // success | failure | null (manual for v0)
        bool? FalseSuccess,
        string? PrimaryFailure); // incorrect_change | broke_existing | violated_constraint | ... | unknown

    public static AttemptVerdict Decide(
        ProcessDriver.DriveResult drive,
        VerifierRunner.VerifyResult verify,
        WorkspacePreparer.FileManifest before,
        WorkspacePreparer.FileManifest after,
        EvalTask task)
    {
        string verification = !verify.Ran ? "error"
            : verify.Checks.Count == 0 ? "error"
            : verify.Checks.Any(c => c.Outcome is "fail") ? "fail"
            : verify.Checks.Any(c => c.Outcome is "inconclusive") ? "fail" : "pass";

        string constraints = EvaluateConstraints(task.Constraints, before, after);
        if (task.Constraints.ReadOnly && !before.Files.Keys.SequenceEqual(after.Files.Keys))
            constraints = "fail";

        string taskOutcome = drive.Outcome != "completed" || verification == "error" ? "inconclusive"
            : verification == "pass" && constraints == "pass" ? "solved" : "failed";

        string? primary = taskOutcome == "solved" ? null
            : drive.Outcome != "completed" ? (drive.Outcome == "timed_out" ? "timeout" : "harness_or_crash")
            : constraints == "fail" ? "violated_constraint"
            : verification == "error" ? "unknown"
            : "incorrect_change";

        return new AttemptVerdict(drive.Outcome, verification, taskOutcome, constraints, null, null, primary);
    }

    private static string EvaluateConstraints(
        EvalConstraints c,
        WorkspacePreparer.FileManifest before,
        WorkspacePreparer.FileManifest after)
    {
        foreach (string pattern in c.MustNotCreate)
            foreach (string f in after.Files.Keys)
                if (GlobMatch(pattern, f) && !before.Files.ContainsKey(f))
                    return "fail";
        foreach (string pattern in c.MustRemainUnchanged)
            foreach (string f in after.Files.Keys)
                if (GlobMatch(pattern, f) && before.Files.TryGetValue(f, out string? h) && after.Files[f] != h)
                    return "fail";
        return "pass";
    }

    private static bool GlobMatch(string pattern, string path)
    {
        // Minimal ** and * support, ordinal.
        string rx = "^" + Regex.Escape(pattern).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(path, rx);
    }

    public static void WriteAll(
        string attemptRoot,
        EvalTask task,
        EvalProfile profile,
        ProcessDriver.DriveResult drive,
        EventParser.ParsedEvents events,
        WorkspacePreparer.FileManifest before,
        WorkspacePreparer.FileManifest after,
        VerifierRunner.VerifyResult verify,
        AttemptVerdict verdict,
        string batchId,
        int attempt)
    {
        File.WriteAllText(Path.Combine(attemptRoot, "stdout.raw.log"), drive.Stdout);
        File.WriteAllText(Path.Combine(attemptRoot, "stderr.raw.log"), drive.Stderr);
        File.WriteAllLines(Path.Combine(attemptRoot, "events.jsonl"), events.JsonLines);
        File.WriteAllText(Path.Combine(attemptRoot, "parser-diagnostics.json"),
            JsonSerializer.Serialize(new { unknown_lines = events.Unknown }, Json));
        File.WriteAllText(Path.Combine(attemptRoot, "initial-manifest.json"),
            JsonSerializer.Serialize(before.Files, Json));
        File.WriteAllText(Path.Combine(attemptRoot, "final-manifest.json"),
            JsonSerializer.Serialize(after.Files, Json));
        File.WriteAllText(Path.Combine(attemptRoot, "changes.json"),
            JsonSerializer.Serialize(DiffManifests(before, after), Json));
        File.WriteAllText(Path.Combine(attemptRoot, "verification.json"),
            JsonSerializer.Serialize(new { ran = verify.Ran, exit = verify.ExitCode, checks = verify.Checks }, Json));
        File.WriteAllText(Path.Combine(attemptRoot, "verifier.stdout.log"), verify.Stdout);
        File.WriteAllText(Path.Combine(attemptRoot, "verifier.stderr.log"), verify.Stderr);
        File.WriteAllText(Path.Combine(attemptRoot, "attempt.json"),
            JsonSerializer.Serialize(new
            {
                batch = batchId,
                task = task.Id,
                fixture = task.FixtureVersion,
                attempt,
                harbor_commit = GitCommit(profile.RepoRoot),
                provider = profile.Provider,
                model = profile.Model,
                // Config fingerprint only: env NAMES, never values (no secrets in artifacts).
                env_names = profile.Harbor.Env.Keys.OrderBy(k => k).ToArray(),
                started_utc = drive.StartedUtc,
                ended_utc = drive.EndedUtc,
                duration_s = (drive.EndedUtc - drive.StartedUtc).TotalSeconds,
                exit_code = drive.ExitCode,
                runner = "Harbor.Evals v0",
            }, Json));
        File.WriteAllText(Path.Combine(attemptRoot, "verdict.json"),
            JsonSerializer.Serialize(verdict, Json));
    }

    private static object DiffManifests(WorkspacePreparer.FileManifest before, WorkspacePreparer.FileManifest after)
    {
        var added = after.Files.Keys.Except(before.Files.Keys, StringComparer.Ordinal).ToArray();
        var removed = before.Files.Keys.Except(after.Files.Keys, StringComparer.Ordinal).ToArray();
        var changed = after.Files.Keys.Intersect(before.Files.Keys, StringComparer.Ordinal)
            .Where(k => after.Files[k] != before.Files[k]).ToArray();
        return new { added, removed, changed };
    }

    private static string GitCommit(string repoRoot)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string? sha = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit();
            return string.IsNullOrWhiteSpace(sha) ? "unknown" : sha;
        }
        catch
        {
            return "unknown";
        }
    }
}
