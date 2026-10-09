using System.Text;

namespace Harbor.Application.Sessions;

/// <summary>
///     One verified check the S5 change report vouches for (epic #42, slice S8):
///     a command that ran at a pinned revision with a recorded exit code.
///     Structured input only — the owner report's Evidence section is rendered
///     from entries like this one, never from agent-supplied prose.
/// </summary>
/// <param name="Command">The exact command that ran (for example <c>dotnet build</c>).</param>
/// <param name="Revision">The commit sha the command ran at.</param>
/// <param name="ExitCode">The process exit code.</param>
public sealed record OwnerVerifiedEntry(string Command, string Revision, int ExitCode);

/// <summary>
///     An explicit gap in verification (the S5 <c>NotVerified</c> case): a check
///     that did not run. Rendered in Evidence as a named gap — never omitted.
/// </summary>
/// <param name="Name">Which check did not run.</param>
/// <param name="Reason">Why it did not run.</param>
public sealed record OwnerNotVerifiedEntry(string Name, string Reason);

/// <summary>
///     One changed rule or rule-file, rendered on its own line inside the Rule
///     changes block as <c>before → after</c> — never inside Evidence.
/// </summary>
/// <param name="Rule">Rule or rule-file name.</param>
/// <param name="Before">Value before the change.</param>
/// <param name="After">Value after the change.</param>
public sealed record OwnerRuleChange(string Rule, string Before, string After);

/// <summary>
///     Owner report (epic #42, slice S8, spec 17 §6): the artifact a human reads
///     to decide whether to keep the work — result → evidence → limits →
///     rule changes → recommendation + rollback.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ResultText" /> and <see cref="EvidenceText" /> are
///         read-only projections: the only construction path is
///         <see cref="Create" />, which builds them from the run manifest
///         fields and the S5/accept-reject audit-trail entries. There is no
///         public constructor, no setter, and no method that accepts section
///         text — operator prose is accepted only for the limits narrative,
///         the recommendation, and the rollback command.
///     </para>
///     <para>
///         Persistence (<c>runs/&lt;RunId&gt;/owner-report.md</c>) and the
///         <c>harbor run owner-report</c> verb belong to later slices; this
///         type is the format plus its structural guard.
///     </para>
/// </remarks>
public sealed class OwnerReport
{
    /// <summary>The fixed signer of every report: the agent prepares, never accepts.</summary>
    public const string PreparedBy = "agent";

    private OwnerReport(
        RunId runId,
        string resultText,
        string evidenceText,
        string limitsText,
        string ruleChangesText,
        string recommendationAndRollbackText,
        string rollbackCommand,
        string acceptedBy)
    {
        RunId = runId;
        ResultText = resultText;
        EvidenceText = evidenceText;
        LimitsText = limitsText;
        RuleChangesText = ruleChangesText;
        RecommendationAndRollbackText = recommendationAndRollbackText;
        RollbackCommand = rollbackCommand;
        AcceptedBy = acceptedBy;
    }

    /// <summary>The run this report describes.</summary>
    public RunId RunId { get; }

    /// <summary>Section 1: read-only projection of the run manifest outcome.</summary>
    public string ResultText { get; }

    /// <summary>Section 2: read-only projection of the S5 verified/not-verified entries.</summary>
    public string EvidenceText { get; }

    /// <summary>Section 3: recorded limits plus the operator's limits narrative.</summary>
    public string LimitsText { get; }

    /// <summary>Section 4: one <c>before → after</c> line per changed rule.</summary>
    public string RuleChangesText { get; }

    /// <summary>Section 5: operator recommendation, rollback command, signatures.</summary>
    public string RecommendationAndRollbackText { get; }

    /// <summary>The validated rollback command (a recognised inverse, see <see cref="OwnerReportFormat.IsRecognisedRollback" />).</summary>
    public string RollbackCommand { get; }

    /// <summary>Who accepted the work. Never the run itself and never the agent (see self-acceptance rule).</summary>
    public string AcceptedBy { get; }

    /// <summary>
    ///     Build an owner report. Every section except the limits narrative,
    ///     the recommendation, and the rollback command is derived from the
    ///     structured inputs; free-form section text cannot be supplied.
    /// </summary>
    /// <param name="runId">The run being reported on (manifest identity).</param>
    /// <param name="outcome">Run outcome from the manifest (result projection source).</param>
    /// <param name="baseRevision">Pinned commit sha from the manifest.</param>
    /// <param name="verified">S5 verified entries quoted verbatim into Evidence.</param>
    /// <param name="notVerified">S5 gaps rendered as explicit <c>NotVerified</c> lines.</param>
    /// <param name="recordedLimits">Recorded run limits (timeout, step budget, …).</param>
    /// <param name="limitsNarrative">Operator prose for Limits. May be empty, never null.</param>
    /// <param name="ruleChanges">Changed rules, rendered as a separate block.</param>
    /// <param name="recommendation">Operator prose. May be empty, never null.</param>
    /// <param name="rollbackCommand">Must be a recognised inverse (patch reverse or S7 worktree removal).</param>
    /// <param name="acceptedBy">Accepting operator. Rejected when it names the run itself or the agent.</param>
    /// <returns>The report, or a failure naming the rejected field.</returns>
    public static Result<OwnerReport> Create(
        RunId runId,
        RunStopReason outcome,
        string baseRevision,
        IReadOnlyList<OwnerVerifiedEntry> verified,
        IReadOnlyList<OwnerNotVerifiedEntry> notVerified,
        IReadOnlyList<string> recordedLimits,
        string limitsNarrative,
        IReadOnlyList<OwnerRuleChange> ruleChanges,
        string recommendation,
        string rollbackCommand,
        string acceptedBy)
    {
        if (runId is null)
            return Result.Failure<OwnerReport>("Run id must not be null.");
        if (string.IsNullOrWhiteSpace(baseRevision))
            return Result.Failure<OwnerReport>("Base revision must not be blank.");
        if (verified is null)
            return Result.Failure<OwnerReport>("Verified entries must not be null.");
        if (notVerified is null)
            return Result.Failure<OwnerReport>("Not-verified entries must not be null.");
        if (recordedLimits is null)
            return Result.Failure<OwnerReport>("Recorded limits must not be null.");
        if (limitsNarrative is null)
            return Result.Failure<OwnerReport>("Limits narrative must not be null (pass empty when there is none).");
        if (ruleChanges is null)
            return Result.Failure<OwnerReport>("Rule changes must not be null.");
        if (recommendation is null)
            return Result.Failure<OwnerReport>("Recommendation must not be null (pass empty when there is none).");
        if (rollbackCommand is null)
            return Result.Failure<OwnerReport>("Rollback command must not be null.");
        if (acceptedBy is null)
            return Result.Failure<OwnerReport>("Accepted by must not be null.");

        for (int i = 0; i < verified.Count; i++)
        {
            OwnerVerifiedEntry entry = verified[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Command) || string.IsNullOrWhiteSpace(entry.Revision))
                return Result.Failure<OwnerReport>($"Verified entry {i} must name a command and a revision.");
        }

        for (int i = 0; i < notVerified.Count; i++)
        {
            OwnerNotVerifiedEntry entry = notVerified[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Reason))
                return Result.Failure<OwnerReport>($"Not-verified entry {i} must name a check and a reason.");
        }

        for (int i = 0; i < recordedLimits.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(recordedLimits[i]))
                return Result.Failure<OwnerReport>($"Recorded limit {i} must not be blank.");
        }

        for (int i = 0; i < ruleChanges.Count; i++)
        {
            OwnerRuleChange change = ruleChanges[i];
            if (change is null
                || string.IsNullOrWhiteSpace(change.Rule)
                || string.IsNullOrWhiteSpace(change.Before)
                || string.IsNullOrWhiteSpace(change.After))
                return Result.Failure<OwnerReport>($"Rule change {i} must name a rule with before and after values.");
            if (string.Equals(change.Before, change.After, StringComparison.Ordinal))
                return Result.Failure<OwnerReport>($"Rule change {i} ('{change.Rule}') does not change anything.");
        }

        if (!OwnerReportFormat.IsRecognisedRollback(rollbackCommand, runId.Value))
            return Result.Failure<OwnerReport>(
                $"Unrecognised rollback command '{rollbackCommand}': "
                + "must be 'git apply --reverse <change.patch>' or the S7 worktree removal "
                + $"('git worktree remove --force runs/{runId.Value}/worktree').");

        string signer = acceptedBy.Trim();
        if (signer.Length == 0)
            return Result.Failure<OwnerReport>("Accepted by must name an operator (or 'pending').");
        if (string.Equals(signer, runId.Value, StringComparison.Ordinal)
            || string.Equals(signer, PreparedBy, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<OwnerReport>(
                $"Self-acceptance is forbidden: 'Accepted by' ('{signer}') must be an operator, "
                + "never the run itself and never the agent.");

        string resultText = $"Run {runId.Value}: {outcome} at {baseRevision.Trim()}";
        string evidenceText = BuildEvidence(verified, notVerified);
        string limitsText = BuildLimits(recordedLimits, limitsNarrative.Trim());
        string ruleChangesText = BuildRuleChanges(ruleChanges);
        string tailText = BuildTail(recommendation.Trim(), rollbackCommand.Trim(), signer);

        return Result.Success(new OwnerReport(
            runId, resultText, evidenceText, limitsText, ruleChangesText, tailText,
            rollbackCommand.Trim(), signer));
    }

    /// <summary>Render the full report: title plus the five sections in fixed order.</summary>
    /// <returns>The report document.</returns>
    public string Render()
    {
        StringBuilder sb = new();
        sb.Append("# Owner report for run ").Append(RunId.Value).Append('\n');
        sb.Append('\n');
        sb.Append(OwnerReportFormat.ResultHeading).Append('\n');
        sb.Append('\n');
        sb.Append(ResultText).Append('\n');
        sb.Append('\n');
        sb.Append(OwnerReportFormat.EvidenceHeading).Append('\n');
        sb.Append('\n');
        sb.Append(EvidenceText).Append('\n');
        sb.Append('\n');
        sb.Append(OwnerReportFormat.LimitsHeading).Append('\n');
        sb.Append('\n');
        sb.Append(LimitsText).Append('\n');
        sb.Append('\n');
        sb.Append(OwnerReportFormat.RuleChangesHeading).Append('\n');
        sb.Append('\n');
        sb.Append(RuleChangesText).Append('\n');
        sb.Append('\n');
        sb.Append(OwnerReportFormat.RecommendationHeading).Append('\n');
        sb.Append('\n');
        sb.Append(RecommendationAndRollbackText).Append('\n');
        return sb.ToString();
    }

    private static string BuildEvidence(
        IReadOnlyList<OwnerVerifiedEntry> verified,
        IReadOnlyList<OwnerNotVerifiedEntry> notVerified)
    {
        if (verified.Count == 0 && notVerified.Count == 0)
            return OwnerReportFormat.NoneMarker;

        StringBuilder sb = new();
        for (int i = 0; i < verified.Count; i++)
        {
            OwnerVerifiedEntry entry = verified[i];
            sb.Append("- ").Append(entry.Command.Trim())
                .Append(" @ ").Append(entry.Revision.Trim())
                .Append(" (exit ").Append(entry.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(')')
                .Append('\n');
        }

        for (int i = 0; i < notVerified.Count; i++)
        {
            OwnerNotVerifiedEntry entry = notVerified[i];
            sb.Append("- NotVerified [").Append(entry.Name.Trim()).Append("]: ").Append(entry.Reason.Trim()).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildLimits(IReadOnlyList<string> recordedLimits, string narrative)
    {
        if (recordedLimits.Count == 0 && narrative.Length == 0)
            return OwnerReportFormat.NoneMarker;

        StringBuilder sb = new();
        for (int i = 0; i < recordedLimits.Count; i++)
            sb.Append("- ").Append(recordedLimits[i].Trim()).Append('\n');

        if (narrative.Length > 0)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(narrative).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildRuleChanges(IReadOnlyList<OwnerRuleChange> ruleChanges)
    {
        if (ruleChanges.Count == 0)
            return OwnerReportFormat.NoneMarker;

        StringBuilder sb = new();
        for (int i = 0; i < ruleChanges.Count; i++)
        {
            OwnerRuleChange change = ruleChanges[i];
            sb.Append("- ").Append(change.Rule.Trim())
                .Append(": ").Append(change.Before.Trim())
                .Append(' ').Append(OwnerReportFormat.RuleArrow).Append(' ')
                .Append(change.After.Trim()).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildTail(string recommendation, string rollbackCommand, string acceptedBy)
    {
        StringBuilder sb = new();
        sb.Append("Recommendation: ");
        sb.Append(recommendation.Length == 0 ? OwnerReportFormat.NoneMarker : recommendation);
        sb.Append('\n');
        sb.Append("Rollback: ").Append(rollbackCommand).Append('\n');
        sb.Append("Prepared by: ").Append(PreparedBy).Append('\n');
        sb.Append("Accepted by: ").Append(acceptedBy);
        return sb.ToString();
    }
}

/// <summary>
///     Structural guard for the owner report (epic #42, slice S8): checks the
///     presence and order of the five sections — never the prose.
/// </summary>
/// <remarks>
///     <para>
///         The guard answers "is this shaped like an owner report?", not "is it
///         well written?". Meaning stays a human job; the machine enforces that
///         no section is omitted or reordered, that rule changes live in their
///         own block, that the rollback is a recognised inverse, and that the
///         run did not accept itself.
///     </para>
/// </remarks>
public static class OwnerReportFormat
{
    /// <summary>Section 1 heading.</summary>
    public const string ResultHeading = "## 1. Result";

    /// <summary>Section 2 heading.</summary>
    public const string EvidenceHeading = "## 2. Evidence";

    /// <summary>Section 3 heading.</summary>
    public const string LimitsHeading = "## 3. Limits";

    /// <summary>Section 4 heading.</summary>
    public const string RuleChangesHeading = "## 4. Rule changes";

    /// <summary>Section 5 heading.</summary>
    public const string RecommendationHeading = "## 5. Recommendation + Rollback";

    /// <summary>Marker printed for an empty section — the section is never omitted.</summary>
    public const string NoneMarker = "(none)";

    /// <summary>Arrow joining before and after on a rule-change line. Only rule lines may carry it.</summary>
    public const string RuleArrow = "→";

    private const string RollbackPrefix = "Rollback: ";
    private const string AcceptedByPrefix = "Accepted by: ";
    private const string PatchInversePrefix = "git apply --reverse ";

    /// <summary>
    ///     Check whether <paramref name="command" /> is a recognised rollback
    ///     inverse: <c>git apply --reverse &lt;change.patch&gt;</c> from the
    ///     frozen set, or the S7 worktree removal for <paramref name="runId" />.
    /// </summary>
    /// <param name="command">Candidate rollback command.</param>
    /// <param name="runId">Run id qualifying the worktree-removal form. Null blanks the form out.</param>
    /// <returns>True for a recognised inverse.</returns>
    public static bool IsRecognisedRollback(string? command, string? runId)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        string candidate = command.Trim();

        if (candidate.StartsWith(PatchInversePrefix, StringComparison.Ordinal))
        {
            string path = candidate.Substring(PatchInversePrefix.Length).Trim();
            if (path.Length == 0 || !path.EndsWith(".patch", StringComparison.Ordinal))
                return false;
            for (int i = 0; i < path.Length; i++)
            {
                char ch = path[i];
                if (ch == ';' || ch == '|' || ch == '&' || ch == '\n' || ch == '\r' || ch == '$' || ch == '`')
                    return false;
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(runId)
            && string.Equals(candidate, "git worktree remove --force runs/" + runId.Trim() + "/worktree", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    ///     Validate the structure of a rendered owner report: all five sections
    ///     present in order, rule changes in their own block, a recognised
    ///     rollback, and no self-acceptance. Prose is never judged.
    /// </summary>
    /// <param name="text">Rendered report text.</param>
    /// <param name="runId">Run id qualifying the worktree-removal rollback form and the self-acceptance check.</param>
    /// <returns>Success, or a failure naming the structural defect.</returns>
    public static Result ValidateRendered(string? text, string? runId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure("Owner report text must not be empty.");

        int result = text.IndexOf(ResultHeading, StringComparison.Ordinal);
        int evidence = text.IndexOf(EvidenceHeading, StringComparison.Ordinal);
        int limits = text.IndexOf(LimitsHeading, StringComparison.Ordinal);
        int rules = text.IndexOf(RuleChangesHeading, StringComparison.Ordinal);
        int tail = text.IndexOf(RecommendationHeading, StringComparison.Ordinal);

        if (result < 0 || evidence < 0 || limits < 0 || rules < 0 || tail < 0)
            return Result.Failure("Owner report must contain all five sections in order: Result, Evidence, Limits, Rule changes, Recommendation + Rollback.");
        if (!(result < evidence && evidence < limits && limits < rules && rules < tail))
            return Result.Failure("Owner report sections are reordered: the fixed order is Result, Evidence, Limits, Rule changes, Recommendation + Rollback.");

        string evidenceBody = Slice(text, evidence + EvidenceHeading.Length, limits);
        string limitsBody = Slice(text, limits + LimitsHeading.Length, rules);
        string rulesBody = Slice(text, rules + RuleChangesHeading.Length, tail);
        string tailBody = text.Substring(tail + RecommendationHeading.Length);

        if (evidenceBody.Trim().Length == 0 || limitsBody.Trim().Length == 0 || rulesBody.Trim().Length == 0)
            return Result.Failure("Owner report sections must never be omitted: an empty section prints '(none)'.");

        if (evidenceBody.Contains(RuleArrow, StringComparison.Ordinal))
            return Result.Failure("Rule changes must be a separate block: no 'before → after' line belongs inside Evidence.");

        if (!string.Equals(rulesBody.Trim(), NoneMarker, StringComparison.Ordinal))
        {
            string[] lines = rulesBody.Split('\n');
            bool seen = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0)
                    continue;
                seen = true;
                if (!lines[i].Contains(RuleArrow, StringComparison.Ordinal))
                    return Result.Failure("Rule changes block lists each changed rule on one line as 'before → after'.");
            }

            if (!seen)
                return Result.Failure("Owner report sections must never be omitted: an empty section prints '(none)'.");
        }

        string? rollback = FindLineValue(tailBody, RollbackPrefix);
        if (rollback is null || !IsRecognisedRollback(rollback, runId))
            return Result.Failure("Owner report rollback must be a recognised inverse: 'git apply --reverse <change.patch>' or the S7 worktree removal.");

        string? acceptedBy = FindLineValue(tailBody, AcceptedByPrefix);
        if (acceptedBy is null || acceptedBy.Trim().Length == 0)
            return Result.Failure("Owner report must be signed 'Accepted by: <operator>' (or 'pending').");
        string signer = acceptedBy.Trim();
        if ((!string.IsNullOrWhiteSpace(runId) && string.Equals(signer, runId.Trim(), StringComparison.Ordinal))
            || string.Equals(signer, OwnerReport.PreparedBy, StringComparison.OrdinalIgnoreCase))
            return Result.Failure($"Self-acceptance is forbidden: 'Accepted by' ('{signer}') must be an operator.");

        return Result.Success();
    }

    private static string Slice(string text, int from, int to) => text.Substring(from, to - from);

    private static string? FindLineValue(string body, string prefix)
    {
        string[] lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line.Substring(prefix.Length);
        }

        return null;
    }
}
