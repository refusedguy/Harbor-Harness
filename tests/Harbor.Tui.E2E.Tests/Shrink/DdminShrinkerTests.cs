using System.Text.RegularExpressions;

namespace Harbor.Tui.E2E.Tests.Shrink;

/// <summary>
///     Contract tests for the ddmin shrinker: fixed order first, determinism
///     gate, budgets, exhaustion behavior, artifact shape, schedule control,
///     and the reduction wording. Source-scan gates follow the
///     CostAnimatorGuardTests pattern: each rule ships with the offending
///     shape it was written against plus a negative control, so a rule that
///     matches nothing cannot rot quietly.
/// </summary>
public class DdminShrinkerTests
{
    private static ShrinkRequest RequestFor(
        IReadOnlyList<UiSequenceAction> sequence, int seed = 7, string commit = "abc1234") =>
        new(sequence, UiStepSchedule.Create(seed, sequence.Count), seed,
            UiSequenceGenerator.GeneratorVersion, commit);

    private static readonly UiSequenceAction[] CancelApproveCommit =
    [
        UiSequenceAction.Cancel, UiSequenceAction.Approve, UiSequenceAction.Commit,
    ];

    // ── fixed order first ────────────────────────────────────────────────

    [Test]
    public async Task DefaultPath_UsesFixedCancelApproveCommitList()
    {
        IReadOnlyList<UiSequenceAction> sequence = UiSequenceGenerator.GenerateDefault();

        await Assert.That(sequence.SequenceEqual(CancelApproveCommit)).IsTrue()
            .Because("the default path is the fixed cancel-approve-commit list");
        await Assert.That(ReferenceEquals(sequence, UiSequence.DefaultFixedOrder)).IsTrue()
            .Because("the default path returns the fixed list itself, not a copy");
    }

    [Test]
    public async Task RandomizedGenerator_IsOptInAndSeedDetermined()
    {
        IReadOnlyList<UiSequenceAction> first = UiSequenceGenerator.GenerateRandomized(11, 8);
        IReadOnlyList<UiSequenceAction> second = UiSequenceGenerator.GenerateRandomized(11, 8);

        await Assert.That(first.SequenceEqual(second)).IsTrue()
            .Because("the same seed must always yield the same sequence");
        await Assert.That(first.Count).IsEqualTo(8);
        await Assert.That(first.All(static action => Enum.IsDefined(action))).IsTrue()
            .Because("every generated step must be a defined action");
        await Assert.That(string.IsNullOrEmpty(UiSequenceGenerator.GeneratorVersion)).IsFalse()
            .Because("the artifact records the generator version; the seed alone is insufficient");
    }

    // ── determinism gate ─────────────────────────────────────────────────

    [Test]
    public async Task SameSeedAndSchedule_ReproducesSameSignature()
    {
        var schedule = UiStepSchedule.Create(7, 3);
        ShrinkCheck check = (candidate, sch, ct) =>
            CheckResult.Fail(string.Join(",", candidate) + "@" + sch.Seed);

        CheckResult first = check(CancelApproveCommit, schedule, CancellationToken.None);
        CheckResult second = check(CancelApproveCommit, schedule, CancellationToken.None);

        await Assert.That(first.IsFailure).IsTrue();
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(second.FailureSignature).IsEqualTo(first.FailureSignature)
            .Because("same seed plus same schedule must reproduce the failure");
    }

    [Test]
    public async Task FlakySequence_RefusesToShrink_WithNonDeterminismFinding()
    {
        int calls = 0;
        ShrinkCheck flaky = (candidate, sch, ct) =>
        {
            calls++;
            return calls == 1 ? CheckResult.Fail("SIG-FLAKY") : CheckResult.Pass();
        };

        ShrinkOutcome outcome =
            new DdminShrinker(new ManualShrinkClock()).TryShrink(RequestFor(CancelApproveCommit), flaky);

        await Assert.That(outcome is Refused).IsTrue()
            .Because("a run whose gate fails is a non-determinism finding, not a shrink result");
        await Assert.That(outcome is Shrunk).IsFalse();
        await Assert.That(calls).IsEqualTo(2)
            .Because("the run refuses before any reduction check");
        await Assert.That(string.IsNullOrEmpty(((Refused)outcome).Finding.Detail)).IsFalse();
    }

    [Test]
    public async Task GateFailure_WhenOriginalPasses_RefusesToShrink()
    {
        ShrinkCheck alwaysPasses = (candidate, sch, ct) => CheckResult.Pass();

        ShrinkOutcome outcome = new DdminShrinker(new ManualShrinkClock())
            .TryShrink(RequestFor(CancelApproveCommit), alwaysPasses);

        await Assert.That(outcome is Refused).IsTrue()
            .Because("there is no failure to shrink when the full sequence passes");
    }

    // ── budgets and exhaustion ───────────────────────────────────────────

    [Test]
    public async Task Reduction_FindsShorterFailingCase_AndKeepsTheSignature()
    {
        ShrinkCheck pairFails = (candidate, sch, ct) =>
        {
            for (int i = 0; i + 1 < candidate.Count; i++)
            {
                if (candidate[i] == UiSequenceAction.Approve &&
                    candidate[i + 1] == UiSequenceAction.Commit)
                {
                    return CheckResult.Fail("SIG-PAIR");
                }
            }

            return CheckResult.Pass();
        };
        UiSequenceAction[] original =
        [
            UiSequenceAction.Cancel, UiSequenceAction.Cancel,
            UiSequenceAction.Approve, UiSequenceAction.Commit,
        ];
        ShrinkRequest request = RequestFor(original);

        ShrinkOutcome outcome =
            new DdminShrinker(new ManualShrinkClock()).TryShrink(request, pairFails);

        await Assert.That(outcome is Shrunk).IsTrue();
        ShrinkReport report = ((Shrunk)outcome).Report;
        await Assert.That(report.BestKnownFailingCase.SequenceEqual(
            new[] { UiSequenceAction.Approve, UiSequenceAction.Commit })).IsTrue()
            .Because("only the approve-commit pair carries the failure");
        await Assert.That(report.StoppedByBudget).IsFalse();
        await Assert.That(report.ChecksPerformed).IsLessThanOrEqualTo(DdminShrinker.MaxShrinkChecks);
        await Assert.That(report.Note).IsEqualTo(DdminShrinker.ReductionNote);

        CheckResult replay = pairFails(report.BestKnownFailingCase, request.Schedule, CancellationToken.None);
        await Assert.That(replay.IsFailure).IsTrue()
            .Because("the returned case must still fail under the same schedule");
        await Assert.That(replay.FailureSignature).IsEqualTo("SIG-PAIR");
    }

    [Test]
    public async Task CheckBudget_StopsAtFiftyChecks_AndReturnsFailingBest()
    {
        IReadOnlyList<UiSequenceAction> original = UiSequenceGenerator.GenerateRandomized(11, 16);
        ShrinkCheck onlyFullFails = (candidate, sch, ct) =>
            candidate.SequenceEqual(original) ? CheckResult.Fail("SIG-COUNT") : CheckResult.Pass();
        ShrinkRequest request = RequestFor(original, seed: 11);

        ShrinkOutcome outcome =
            new DdminShrinker(new ManualShrinkClock()).TryShrink(request, onlyFullFails);

        await Assert.That(outcome is Shrunk).IsTrue();
        ShrinkReport report = ((Shrunk)outcome).Report;
        await Assert.That(report.ChecksPerformed).IsLessThanOrEqualTo(DdminShrinker.MaxShrinkChecks)
            .Because("a run past the check bound must stop");
        await Assert.That(report.StoppedByBudget).IsTrue()
            .Because("a 16-step irreducible case needs more than the bound allows");
        await Assert.That(report.BestKnownFailingCase.SequenceEqual(original)).IsTrue();

        CheckResult replay =
            onlyFullFails(report.BestKnownFailingCase, request.Schedule, CancellationToken.None);
        await Assert.That(replay.IsFailure).IsTrue()
            .Because("exhaustion returns the best-known failing case, never a passing one");
        await Assert.That(replay.FailureSignature).IsEqualTo("SIG-COUNT");
        await Assert.That(report.Note).Contains(DdminShrinker.ReductionNote);
    }

    [Test]
    public async Task TimeBudget_MeasuredFromRunStart_StopsAndKeepsFailingBest()
    {
        var clock = new ManualShrinkClock();
        ShrinkCheck slow = (candidate, sch, ct) =>
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            return CheckResult.Fail("SIG-TIME");
        };

        ShrinkOutcome outcome =
            new DdminShrinker(clock).TryShrink(RequestFor(CancelApproveCommit), slow);

        await Assert.That(outcome is Shrunk).IsTrue();
        ShrinkReport report = ((Shrunk)outcome).Report;
        await Assert.That(report.ChecksPerformed).IsEqualTo(2)
            .Because("the gate replays twice, then the 30 s window from run start is already spent");
        await Assert.That(report.StoppedByBudget).IsTrue();
        await Assert.That(report.Elapsed.TotalSeconds).IsGreaterThanOrEqualTo(30.0);
        await Assert.That(report.BestKnownFailingCase.SequenceEqual(CancelApproveCommit)).IsTrue()
            .Because("the gate verified the full sequence, so it is the best-known failing case");
    }

    [Test]
    public async Task PerCheckToken_CarriesRemainingBudget()
    {
        var seen = new List<bool>();
        ShrinkCheck recording = (candidate, sch, ct) =>
        {
            seen.Add(ct.CanBeCanceled);
            return CheckResult.Fail("SIG-TOK");
        };
        UiSequenceAction[] pair = [UiSequenceAction.Cancel, UiSequenceAction.Approve];

        ShrinkOutcome outcome =
            new DdminShrinker(new ManualShrinkClock()).TryShrink(RequestFor(pair), recording);

        await Assert.That(outcome is Shrunk).IsTrue();
        await Assert.That(seen.Count).IsEqualTo(((Shrunk)outcome).Report.ChecksPerformed)
            .Because("every check runs under the run budget");
        await Assert.That(seen.All(cancelable => cancelable)).IsTrue()
            .Because("each check receives a token carrying the remaining run budget, " +
                     "so one hung cooperative check cannot blow the duration bound");
    }

    // ── artifact ─────────────────────────────────────────────────────────

    [Test]
    public async Task Artifact_ContainsAllFiveFields()
    {
        FailureArtifact artifact = FailureArtifact.Create(
            7, UiSequence.DefaultFixedOrder, UiSequenceGenerator.GeneratorVersion, "SIG-A", "deadbeef");

        await Assert.That(artifact.Seed).IsEqualTo(7);
        await Assert.That(string.IsNullOrEmpty(artifact.FullSequence)).IsFalse();
        await Assert.That(artifact.FullSequence).Contains("Approve");
        await Assert.That(string.IsNullOrEmpty(artifact.GeneratorVersion)).IsFalse();
        await Assert.That(artifact.FailureSignature).IsEqualTo("SIG-A");
        await Assert.That(string.IsNullOrEmpty(artifact.CommitSha)).IsFalse();
    }

    [Test]
    public async Task Artifact_MissingGeneratorVersion_Throws()
    {
        // A missing generator version fails the artifact, by contract.
        await Assert.That(() => FailureArtifact.Create(
                7, UiSequence.DefaultFixedOrder, string.Empty, "SIG-A", "deadbeef"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ShrinkReport_ToArtifact_RoundTrips()
    {
        ShrinkCheck pairFails = (candidate, sch, ct) =>
        {
            for (int i = 0; i + 1 < candidate.Count; i++)
            {
                if (candidate[i] == UiSequenceAction.Approve &&
                    candidate[i + 1] == UiSequenceAction.Commit)
                {
                    return CheckResult.Fail("SIG-PAIR");
                }
            }

            return CheckResult.Pass();
        };
        UiSequenceAction[] original =
        [
            UiSequenceAction.Cancel, UiSequenceAction.Cancel,
            UiSequenceAction.Approve, UiSequenceAction.Commit,
        ];
        ShrinkRequest request = RequestFor(original, seed: 9, commit: "cafe01");
        ShrinkOutcome outcome =
            new DdminShrinker(new ManualShrinkClock()).TryShrink(request, pairFails);

        await Assert.That(outcome is Shrunk).IsTrue();
        FailureArtifact artifact = ((Shrunk)outcome).Report.ToArtifact(request);

        await Assert.That(artifact.Seed).IsEqualTo(9);
        await Assert.That(artifact.FullSequence).IsEqualTo("Approve,Commit");
        await Assert.That(artifact.GeneratorVersion).IsEqualTo(UiSequenceGenerator.GeneratorVersion);
        await Assert.That(artifact.FailureSignature).IsEqualTo("SIG-PAIR");
        await Assert.That(artifact.CommitSha).IsEqualTo("cafe01");
    }

    // ── schedule control ─────────────────────────────────────────────────

    [Test]
    public async Task Schedule_IsReproducible_WithFixedVirtualClock()
    {
        UiStepSchedule first = UiStepSchedule.Create(42, 5);
        UiStepSchedule second = UiStepSchedule.Create(42, 5);

        await Assert.That(first.StepDelays.SequenceEqual(second.StepDelays)).IsTrue()
            .Because("the schedule is derived from the seed, not from wall-clock luck");

        IReadOnlyList<DateTime> replayOne = first.ReplayOn(new ManualShrinkClock());
        IReadOnlyList<DateTime> replayTwo = second.ReplayOn(new ManualShrinkClock());

        await Assert.That(replayOne.SequenceEqual(replayTwo)).IsTrue()
            .Because("two replays on fresh virtual clocks observe identical timestamps");
        await Assert.That(replayOne.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Shrinker_PassesSameSchedule_ToEveryCheck()
    {
        ShrinkRequest request = RequestFor(CancelApproveCommit);
        var seenSeeds = new List<int>();
        var seenLengths = new List<int>();
        ShrinkCheck recording = (candidate, sch, ct) =>
        {
            seenSeeds.Add(sch.Seed);
            seenLengths.Add(sch.StepDelays.Count);
            return CheckResult.Fail("SIG-S");
        };

        ShrinkOutcome outcome =
            new DdminShrinker(new ManualShrinkClock()).TryShrink(request, recording);

        await Assert.That(outcome is Shrunk).IsTrue();
        await Assert.That(seenSeeds.Count).IsEqualTo(((Shrunk)outcome).Report.ChecksPerformed);
        await Assert.That(seenSeeds.All(seed => seed == request.Schedule.Seed)).IsTrue();
        await Assert.That(seenLengths.All(length => length == request.Schedule.StepDelays.Count)).IsTrue();
    }

    // ── wording and source scans ─────────────────────────────────────────

    [Test]
    public async Task Implementation_HasNoShrinkFailureVerdict()
    {
        string? root = FindRepoRoot();
        await Assert.That(root).IsNotNull()
            .Because("the perimeter must be reachable for this gate to mean anything");

        string shrinkDir = Path.Combine(root!, "tests", "Harbor.Tui.E2E.Tests", "Shrink");
        await Assert.That(Directory.Exists(shrinkDir)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(shrinkDir, "DdminShrinker.cs"))).IsTrue()
            .Because("DdminShrinker is the type this gate is about; if it moved, re-point this gate");

        // Built from parts so this very file does not contain the banned verdict.
        string banned = string.Concat("shrink", "ing failed");
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.GetFiles(shrinkDir, "*.cs"))
        {
            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(banned, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }

        await Assert.That(scanned).IsGreaterThanOrEqualTo(5)
            .Because("an empty scan makes the rule vacuously green");
        await Assert.That(offenders).IsEmpty()
            .Because("exhaustion returns the best-known failing case; the run itself has no failure verdict");

        await Assert.That(("result = \"" + banned + "\";").Contains(banned, StringComparison.OrdinalIgnoreCase))
            .IsTrue().Because("the rule must still match the verdict it was written against");
        await Assert.That("shrinking did not start".Contains(banned, StringComparison.OrdinalIgnoreCase))
            .IsFalse().Because("the refusal phrasing is not the banned verdict");
    }

    [Test]
    public async Task ContractSources_ClaimNoUnqualifiedOptimality()
    {
        string? root = FindRepoRoot();
        await Assert.That(root).IsNotNull()
            .Because("the perimeter must be reachable for this gate to mean anything");

        // Built from parts so this very file does not contain the banned claim.
        var unqualified = new Regex("\\b" + "mini" + "mal" + "\\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var paths = new List<string>(Directory.GetFiles(
            Path.Combine(root!, "tests", "Harbor.Tui.E2E.Tests", "Shrink"), "*.cs"));
        string contract = Path.Combine(root!, "docs", "DDMIN_SHRINK_CONTRACT.md");
        await Assert.That(File.Exists(contract)).IsTrue()
            .Because("the contract document is part of this slice; if it moved, re-point this gate");
        paths.Add(contract);

        var offenders = new List<string>();
        foreach (string path in paths)
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (unqualified.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        await Assert.That(paths.Count).IsGreaterThanOrEqualTo(6)
            .Because("an empty scan makes the rule vacuously green");
        await Assert.That(offenders).IsEmpty()
            .Because("the only reduction claim is the qualified one named below");

        await Assert.That(unqualified.IsMatch("the " + "mini" + "mal" + " failing case")).IsTrue()
            .Because("the rule must still match the claim it was written against");
        await Assert.That(unqualified.IsMatch("Math.Min(granularity * 2, current.Count)")).IsFalse()
            .Because("an arithmetic helper is not an optimality claim");
        await Assert.That(unqualified.IsMatch(DdminShrinker.ReductionNote)).IsFalse()
            .Because("the contract wording carries no unqualified claim");
    }

    [Test]
    public async Task ContractDocument_StatesTheReductionWording()
    {
        string? root = FindRepoRoot();
        await Assert.That(root).IsNotNull();

        string contract = Path.Combine(root!, "docs", "DDMIN_SHRINK_CONTRACT.md");
        string text = File.ReadAllText(contract);

        await Assert.That(text).Contains(DdminShrinker.ReductionNote)
            .Because("user-facing and doc output use the qualified reduction wording");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Harbor.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
