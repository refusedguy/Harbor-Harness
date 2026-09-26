using System.Text.Json;

namespace Harbor.Evals;

/// <summary>Batch summary: solved share, constraint share, costs, failure classes.</summary>
internal static class SummaryBuilder
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed record AttemptSummary(
        string Task,
        int Attempt,
        string TaskOutcome,
        string ConstraintsOutcome,
        string Execution,
        string? PrimaryFailure,
        double DurationS);

    public static void Write(string resultsRoot, string batchId, List<AttemptSummary> attempts)
    {
        int valid = attempts.Count(a => a.TaskOutcome is "solved" or "failed");
        int solved = attempts.Count(a => a.TaskOutcome == "solved");
        int constrained = attempts.Count(a => a.ConstraintsOutcome == "pass");
        var byFailure = attempts.Where(a => a.PrimaryFailure is not null)
            .GroupBy(a => a.PrimaryFailure!).ToDictionary(g => g.Key, g => g.Count());

        var summary = new
        {
            batch = batchId,
            attempts = attempts.Count,
            solved_share = valid == 0 ? (double?)null : (double)solved / valid,
            constraint_pass_share = attempts.Count == 0 ? (double?)null : (double)constrained / attempts.Count,
            failures = byFailure,
            // Cost accounting plugs in when usage.json exists per attempt.
            // Unknown cost stays null per spec; a null total means unmeasured.
            full_cost_to_success = (decimal?)null,
            cost_coverage = 0.0,
            items = attempts,
        };
        string dir = Path.Combine(resultsRoot, batchId);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(summary, Json));

        var md = new System.Text.StringBuilder().AppendLine($"# Eval batch {batchId}").AppendLine();
        md.AppendLine($"- attempts: {attempts.Count}, solved share: {(summary.solved_share?.ToString("P1") ?? "n/a")}");
        md.AppendLine($"- constraint pass: {(summary.constraint_pass_share?.ToString("P1") ?? "n/a")}");
        foreach (var kv in byFailure)
            md.AppendLine($"- {kv.Key}: {kv.Value}");
        File.WriteAllText(Path.Combine(dir, "SUMMARY.md"), md.ToString());
    }
}
