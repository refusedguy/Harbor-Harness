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
        string Verification,
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
        var claims = ReadManualClaims(Path.Combine(resultsRoot, batchId));

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
            // Manual agent-claim annotation (see evals/README.md): a human sets
            // agentClaim/falseSuccess in verdict.json post-hoc. No claim ever
            // comes from the agent transcript (no regex on success claims).
            false_success_share = claims.Share,
            claims_coverage = claims.Coverage,
            items = attempts,
        };
        string dir = Path.Combine(resultsRoot, batchId);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(summary, Json));

        var md = new System.Text.StringBuilder().AppendLine($"# Eval batch {batchId}").AppendLine();
        md.AppendLine($"- attempts: {attempts.Count}, solved share: {(summary.solved_share?.ToString("P1") ?? "n/a")}");
        md.AppendLine($"- constraint pass: {(summary.constraint_pass_share?.ToString("P1") ?? "n/a")}");
        md.AppendLine($"- false-success share: {(summary.false_success_share?.ToString("P1") ?? "n/a (no manual claims recorded)")}");
        md.AppendLine($"- full cost to success: unknown (usage.json not wired in v0)");
        foreach (var kv in byFailure)
            md.AppendLine($"- {kv.Key}: {kv.Value}");
        md.AppendLine();
        md.AppendLine("| task | attempt | verdict | constraints | verify | exec | failure | duration_s | cost |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var a in attempts)
            md.AppendLine($"| {a.Task} | {a.Attempt} | {a.TaskOutcome} | {a.ConstraintsOutcome} | {a.Verification} | {a.Execution} | {a.PrimaryFailure ?? "—"} | {a.DurationS:F1} | unknown |");
        File.WriteAllText(Path.Combine(dir, "SUMMARY.md"), md.ToString());
    }

    /// <summary>Rebuilds summary.json/SUMMARY.md for a finished batch from
    /// its attempt.json + verdict.json files (no re-run). Used after manual
    /// agentClaim annotation.</summary>
    public static int Resummarize(string resultsRoot, string batchId)
    {
        var items = new List<AttemptSummary>();
        string dir = Path.Combine(resultsRoot, batchId);
        if (!Directory.Exists(dir))
            return 0;
        foreach (string attemptDir in Directory.GetDirectories(dir, "attempt-*", SearchOption.AllDirectories))
        {
            try
            {
                var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(attemptDir, "attempt.json"))).RootElement;
                var verdict = JsonDocument.Parse(File.ReadAllText(Path.Combine(attemptDir, "verdict.json"))).RootElement;
                items.Add(new AttemptSummary(
                    attempt.GetProperty("task").GetString() ?? "?",
                    attempt.GetProperty("attempt").GetInt32(),
                    verdict.GetProperty("TaskOutcome").GetString() ?? "inconclusive",
                    verdict.GetProperty("ConstraintsOutcome").GetString() ?? "not_evaluated",
                    verdict.GetProperty("Verification").GetString() ?? "error",
                    verdict.GetProperty("Execution").GetString() ?? "harness_error",
                    verdict.GetProperty("PrimaryFailure").ValueKind == JsonValueKind.String
                        ? verdict.GetProperty("PrimaryFailure").GetString() : null,
                    attempt.TryGetProperty("duration_s", out var d) ? d.GetDouble() : 0));
            }
            catch
            {
                // Incomplete attempt dir: skip, never fail the summary.
            }
        }

        items.Sort((a, b) => string.Compare(a.Task, b.Task, StringComparison.Ordinal) != 0
            ? string.Compare(a.Task, b.Task, StringComparison.Ordinal)
            : a.Attempt.CompareTo(b.Attempt));
        Write(resultsRoot, batchId, items);
        return items.Count;
    }

    /// <summary>Re-reads verdict.json files of this batch for human-annotated
    /// agentClaim/falseSuccess (manual in v0 — never inferred from logs).</summary>
    private static (double? Share, double Coverage) ReadManualClaims(string batchDir)
    {
        if (!Directory.Exists(batchDir))
            return (null, 0.0);
        int claimed = 0, falseSucc = 0, total = 0;
        foreach (string f in Directory.GetFiles(batchDir, "verdict.json", SearchOption.AllDirectories))
        {
            total++;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                bool hasClaim = root.TryGetProperty("AgentClaim", out var claim)
                    && claim.ValueKind == JsonValueKind.String;
                // Be liberal: accept camelCase too (hand-edited files).
                if (!hasClaim)
                    hasClaim = root.TryGetProperty("agentClaim", out claim)
                        && claim.ValueKind == JsonValueKind.String;
                if (!hasClaim)
                    continue;
                claimed++;
                if (root.TryGetProperty("falseSuccess", out var fs) && fs.ValueKind == JsonValueKind.True)
                    falseSucc++;
                else if (root.TryGetProperty("FalseSuccess", out fs) && fs.ValueKind == JsonValueKind.True)
                    falseSucc++;
            }
            catch
            {
                // Unreadable verdict: counts in denominator, never as a claim.
            }
        }

        double? share = claimed == 0 ? null : (double)falseSucc / claimed;
        double coverage = total == 0 ? 0.0 : (double)claimed / total;
        return (share, coverage);
    }
}
