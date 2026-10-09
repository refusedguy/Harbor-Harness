using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     S8 (#392, spec 17 §6): the owner report renders five sections in fixed
///     order, rule changes stay out of Evidence, Result/Evidence have no public
///     write path, the rollback is a recognised inverse, and the run cannot
///     accept itself. The guard checks structure — never prose.
/// </summary>
public class OwnerReportTests
{
    private const string Run = "abc123";
    private const string Rev = "cafef00dcafef00dcafef00dcafef00dcafef00d";
    private const string PatchRollback = "git apply --reverse runs/abc123/change.patch";

    private static Result<OwnerReport> Create(
        string acceptedBy = "operator",
        string rollback = PatchRollback,
        IReadOnlyList<OwnerVerifiedEntry>? verified = null,
        IReadOnlyList<OwnerNotVerifiedEntry>? notVerified = null,
        IReadOnlyList<string>? recordedLimits = null,
        string narrative = "Limits narrative.",
        IReadOnlyList<OwnerRuleChange>? ruleChanges = null,
        string recommendation = "Keep it.") =>
        OwnerReport.Create(
            RunId.Create(Run),
            RunStopReason.Succeeded,
            Rev,
            verified ?? new List<OwnerVerifiedEntry> { new("dotnet build", Rev, 0) },
            notVerified ?? new List<OwnerNotVerifiedEntry>(),
            recordedLimits ?? new List<string> { "timeout 300s" },
            narrative,
            ruleChanges ?? new List<OwnerRuleChange> { new("perm:bash", "deny", "ask") },
            recommendation,
            rollback,
            acceptedBy);

    [Test]
    public async Task Render_SectionsInFixedOrder()
    {
        var result = Create();
        await Assert.That(result.IsSuccess).IsTrue();
        string text = result.Value.Render();

        int r = text.IndexOf(OwnerReportFormat.ResultHeading, StringComparison.Ordinal);
        int e = text.IndexOf(OwnerReportFormat.EvidenceHeading, StringComparison.Ordinal);
        int l = text.IndexOf(OwnerReportFormat.LimitsHeading, StringComparison.Ordinal);
        int c = text.IndexOf(OwnerReportFormat.RuleChangesHeading, StringComparison.Ordinal);
        int t = text.IndexOf(OwnerReportFormat.RecommendationHeading, StringComparison.Ordinal);

        await Assert.That(r >= 0 && r < e && e < l && l < c && c < t).IsTrue();
    }

    [Test]
    public async Task Render_EmptySectionsPrintNone()
    {
        var result = Create(
            verified: new List<OwnerVerifiedEntry>(),
            notVerified: new List<OwnerNotVerifiedEntry>(),
            recordedLimits: new List<string>(),
            narrative: string.Empty,
            ruleChanges: new List<OwnerRuleChange>(),
            recommendation: string.Empty);

        await Assert.That(result.IsSuccess).IsTrue();
        string text = result.Value.Render();

        await Assert.That(text.Contains(OwnerReportFormat.NoneMarker)).IsTrue();
        await Assert.That(text.Contains(OwnerReportFormat.ResultHeading)).IsTrue();
        await Assert.That(text.Contains(OwnerReportFormat.EvidenceHeading)).IsTrue();
        await Assert.That(text.Contains(OwnerReportFormat.LimitsHeading)).IsTrue();
        await Assert.That(text.Contains(OwnerReportFormat.RuleChangesHeading)).IsTrue();
        await Assert.That(text.Contains(OwnerReportFormat.RecommendationHeading)).IsTrue();

        var validated = OwnerReportFormat.ValidateRendered(text, Run);
        await Assert.That(validated.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ValidateRendered_RuleChangesInsideEvidence_Fails()
    {
        var result = Create();
        await Assert.That(result.IsSuccess).IsTrue();
        string text = result.Value.Render();

        string tampered = text.Replace(
            OwnerReportFormat.EvidenceHeading,
            OwnerReportFormat.EvidenceHeading + "\n- perm:bash: deny → ask",
            StringComparison.Ordinal);

        var validated = OwnerReportFormat.ValidateRendered(tampered, Run);
        await Assert.That(validated.IsFailure).IsTrue();
        await Assert.That(validated.Error.Contains("separate block")).IsTrue();
    }

    [Test]
    public async Task ValidateRendered_ValidReport_Passes()
    {
        var result = Create();
        await Assert.That(result.IsSuccess).IsTrue();

        var validated = OwnerReportFormat.ValidateRendered(result.Value.Render(), Run);
        await Assert.That(validated.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ResultAndEvidence_HaveNoPublicWritePath()
    {
        Type type = typeof(OwnerReport);

        await Assert.That(type.GetConstructors().Length).IsEqualTo(0);

        PropertyInfo? resultProp = type.GetProperty("ResultText");
        PropertyInfo? evidenceProp = type.GetProperty("EvidenceText");
        await Assert.That(resultProp is not null && evidenceProp is not null).IsTrue();
        await Assert.That(resultProp!.GetSetMethod() is null).IsTrue();
        await Assert.That(evidenceProp!.GetSetMethod() is null).IsTrue();

        int factories = 0;
        bool createSeen = false;
        MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < methods.Length; i++)
        {
            if (methods[i].ReturnType == typeof(Result<OwnerReport>))
            {
                factories++;
                createSeen = createSeen || methods[i].Name == nameof(OwnerReport.Create);
            }
        }

        await Assert.That(factories).IsEqualTo(1);
        await Assert.That(createSeen).IsTrue();
    }

    [Test]
    public async Task Create_UnrecognisedRollback_Rejected()
    {
        var bad = Create(rollback: "rm -rf /tmp/x");
        await Assert.That(bad.IsFailure).IsTrue();
        await Assert.That(bad.Error.Contains("rollback")).IsTrue();

        var wrongSuffix = Create(rollback: "git apply --reverse change.txt");
        await Assert.That(wrongSuffix.IsFailure).IsTrue();

        var foreignWorktree = Create(rollback: "git worktree remove --force runs/other/worktree");
        await Assert.That(foreignWorktree.IsFailure).IsTrue();

        var patch = Create(rollback: PatchRollback);
        await Assert.That(patch.IsSuccess).IsTrue();

        var worktree = Create(rollback: "git worktree remove --force runs/abc123/worktree");
        await Assert.That(worktree.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Create_SelfAcceptance_Rejected()
    {
        var self = Create(acceptedBy: Run);
        await Assert.That(self.IsFailure).IsTrue();
        await Assert.That(self.Error.Contains("Self-acceptance")).IsTrue();

        var agent = Create(acceptedBy: "Agent");
        await Assert.That(agent.IsFailure).IsTrue();

        var blank = Create(acceptedBy: "  ");
        await Assert.That(blank.IsFailure).IsTrue();

        var op = Create(acceptedBy: "operator");
        await Assert.That(op.IsSuccess).IsTrue();

        var pending = Create(acceptedBy: "pending");
        await Assert.That(pending.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Evidence_QuotesS5RevisionsVerbatim()
    {
        var result = Create(
            verified: new List<OwnerVerifiedEntry> { new("dotnet build --no-restore", Rev, 5) },
            notVerified: new List<OwnerNotVerifiedEntry> { new("e2e-live", "no model key in CI") });
        await Assert.That(result.IsSuccess).IsTrue();
        string text = result.Value.Render();

        await Assert.That(text.Contains(Rev)).IsTrue();
        await Assert.That(text.Contains("dotnet build --no-restore")).IsTrue();
        await Assert.That(text.Contains("exit 5")).IsTrue();
        await Assert.That(text.Contains("NotVerified")).IsTrue();
        await Assert.That(text.Contains("no model key in CI")).IsTrue();
    }

    [Test]
    public async Task Render_MatchesSectionProperties()
    {
        var result = Create();
        await Assert.That(result.IsSuccess).IsTrue();
        OwnerReport report = result.Value;
        string text = report.Render();

        await Assert.That(text.Contains(report.ResultText)).IsTrue();
        await Assert.That(text.Contains(report.EvidenceText)).IsTrue();
        await Assert.That(text.Contains(report.LimitsText)).IsTrue();
        await Assert.That(text.Contains(report.RuleChangesText)).IsTrue();
        await Assert.That(text.Contains(report.RecommendationAndRollbackText)).IsTrue();
    }
}
