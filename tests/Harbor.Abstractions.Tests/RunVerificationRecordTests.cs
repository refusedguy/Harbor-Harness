// RunVerificationRecordTests.cs — epic #41 slice B3.1 (#405).
//
// WHAT IS BEING CLAIMED HERE
// --------------------------
// A verification record is a FACT about a run, not an opinion about one. Every
// member it holds is something that was observed — a commit, a command, an exit
// code, a moment — so a reader can re-run a check and compare. A record whose
// only content is the word "verified" cannot be re-checked, and a month later it
// describes a tree nobody ran anything against.
//
// The three tests that carry that claim are the ones a plausible-zero
// implementation fails, and they are named as such:
//
//   * Pin_ZeroChecks_IsNotVerified_RatherThanVerified — the empty record must
//     NOT be the flattering verdict. This is #993's shape one layer up: an
//     empty history reporting Succeeded for free.
//   * WithCheck_PassedOnADifferentRevision_DoesNotVerify_AndIsReportedAsNotVerified
//     — the anti-staleness mechanism, and the test that fails if the revision
//     comparison is ever dropped: a pass on some other commit is not a pass on
//     the pinned tree, and it must be NAMED rather than dropped.
//   * UnreadableEvidence_AssertsNoVerdict_AndDoesNotCollapseIntoNotVerified —
//     the epistemic axis stays separate from the verdict axis, and the two print
//     different sentences. This is #782's shape (a plausible value standing in
//     for a missing fact) one level down.
//
// NO REAL TEST SUITE IS INVOKED. The check runner does not exist yet — building
// it is #42's "isolated work + checks" bullet — so a check is SUPPLIED here, and
// the throwing/timing-out runner below is the fake the slice calls for. The one
// filesystem fact this file asserts is the existence of a temp file it created
// itself, which is the artifact-existence rule, not a test suite.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Epic #41 slice B3.1: what was verified about a run, and what the record
///     refuses to claim.
/// </summary>
public class RunVerificationRecordTests
{
    private const string Pinned = "1111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222";
    private const string Env = "linux-10.0/net10.0";
    private static readonly DateTimeOffset Sealed = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Took = TimeSpan.FromSeconds(12);

    private static WorkspaceContract Contract(string baseRevision = Pinned) => new(
        RepoRoot: "/proj",
        BaseRevision: baseRevision,
        BaseBranch: "dev",
        TrackedDirt: [],
        UntrackedPresent: false,
        Isolation: WorkspaceIsolation.Copy,
        Limits: new WorkspaceLimits());

    private static RunCheck Pass(string revision = Pinned) =>
        RunCheck.Passed("dotnet run --project tests/X", 0, revision, Env, Took);

    private static RunCheck Fail(string revision = Pinned) =>
        RunCheck.Failed("dotnet run --project tests/X", 1, revision, Env, Took);

    // ---- the three verdicts, each asserted from a state that produces it ----

    [Test]
    public async Task OnePassedCheckAndNoFailure_Verifies()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed).WithCheck(Pass());

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.Verified);
        await Assert.That(record.Verification.IsVerified).IsTrue();
        await Assert.That(record.Verification.Evidence).IsEqualTo(RunEvidenceState.Complete);
    }

    [Test]
    public async Task OneFailedCheck_FailsVerification_AndAFailOutranksAPass()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithCheck(Pass())
            .WithCheck(Fail());

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.VerificationFailed);
        await Assert.That(record.Verification.IsVerified).IsFalse().Because(
            "\"at least one passed\" is not the rule the slice set. The rule is \"at least one passed AND "
            + "none failed\", because a record that says verified while a gate is red is the #859 cell and "
            + "the #942 $0.0000: a number that reads as a result and is not one.");
    }

    [Test]
    public async Task Pin_ZeroChecks_IsNotVerified_RatherThanVerified()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed);

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.NotVerified);
        await Assert.That(record.Verification.IsVerified).IsFalse();
        await Assert.That(record.Summary.Contains("not verified", StringComparison.Ordinal)).IsTrue().Because(
            "the empty record is the state `harbor run task` prints today, and it is the state most likely "
            + "to be tuned into the flattering answer. #993 found the same collapse in the execution axis — "
            + "an empty history reporting Succeeded off the end of a catch-all — so this is asserted from "
            + "the empty side deliberately.");
    }

    // ---- a check that COULD NOT run is not-verified with a reason, not a pass ----

    [Test]
    public async Task ACheckThatCouldNotRun_IsNotAPass_AndCarriesItsReason()
    {
        // The shape a fake runner produces when the tool is missing or the
        // workspace was not isolated: the command never produced an exit code.
        RunCheck skipped = RunCheck.DidNotRun(
            "dotnet run --project tests/Integration",
            "dotnet is not on PATH in the isolated copy",
            Pinned,
            Env,
            TimeSpan.Zero);

        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithCheck(Pass())
            .WithCheck(skipped);

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.Verified).Because(
            "a check that never ran is not a failure, and the slice's rule counts passes and failures "
            + "only. The reason it is NOT evidence is expressed by it contributing nothing, and it is "
            + "kept in Checks rather than dropped.");

        await Assert.That(skipped.ExitCode).IsNull();
        await Assert.That(skipped.Reason).IsEqualTo("dotnet is not on PATH in the isolated copy");
        await Assert.That(record.Checks.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task ACheckThatCouldNotRun_Alone_LeavesTheRunNotVerified()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithCheck(RunCheck.DidNotRun("make integration", "timed out after 600s", Pinned, Env, TimeSpan.FromSeconds(600)));

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.NotVerified);
        await Assert.That(record.Verification.IsVerified).IsFalse();
    }

    [Test]
    public async Task ACheckThatCouldNotRun_WithoutAReason_IsRefused()
    {
        // "It did not run" and "nobody tried" are the same record if the reason
        // is optional, and the second one is a lie about the run.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => RunCheck.DidNotRun("make integration", "   ", Pinned, Env, TimeSpan.Zero));

        await Assert.That(ex.Message.Contains("nobody tried", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task NotVerified_IsNeverOmitted_WhenACheckCouldNotBeTrusted()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithNotVerified("the integration suite", "it needs a database, and the run has no docker");

        await Assert.That(record.NotVerified.Count()).IsEqualTo(1);
        await Assert.That(record.NotVerified[0].Describe()).IsEqualTo(
            "the integration suite: it needs a database, and the run has no docker");
        await Assert.That(record.Summary.Contains("not verified - the integration suite", StringComparison.Ordinal))
            .IsTrue().Because(
            "the list is only worth having if the printed line carries it. A record that computed an "
            + "omission and did not print it is the same loss of information as not recording it.");
    }

    // ---- the anti-staleness mechanism: a pass on another tree is not a pass here ----

    [Test]
    public async Task WithCheck_PassedOnADifferentRevision_DoesNotVerify_AndIsReportedAsNotVerified()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(Pinned), Sealed)
            .WithCheck(Pass(Other));

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.NotVerified).Because(
            "this is the whole reason the record carries a revision per check instead of a verdict. A "
            + "green build on commit 2222 says nothing about the tree this run was pinned to (1111), and "
            + "counting it would make the record describe a commit nobody tested.");

        await Assert.That(record.Verification.IsVerified).IsFalse();
        await Assert.That(record.Checks.Count()).IsEqualTo(1).Because(
            "the check is kept, not dropped. Dropping it is how a record becomes a summary of the checks "
            + "that flattered it.");
        await Assert.That(record.NotVerified.Count()).IsEqualTo(1);
        await Assert.That(record.NotVerified[0].What).IsEqualTo("dotnet run --project tests/X");
        await Assert.That(record.NotVerified[0].Reason.Contains(Other, StringComparison.Ordinal)).IsTrue();
        await Assert.That(record.NotVerified[0].Reason.Contains(Pinned, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AFailedCheckOnAnotherRevision_AlsoDoesNotFailThisRun()
    {
        // Symmetric, and the reason is the same one: the verdict is about the
        // pinned tree, so a foreign failure is neither a pass nor a fail HERE.
        // It is still reported, so the reader is not left believing the gate ran.
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(Pinned), Sealed)
            .WithCheck(Pass(Pinned))
            .WithCheck(Fail(Other));

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.Verified);
        await Assert.That(record.NotVerified.Count()).IsEqualTo(1);
    }

    // ---- Unknown is an epistemic marker, and it does not collapse ----

    [Test]
    public async Task UnreadableEvidence_AssertsNoVerdict_AndDoesNotCollapseIntoNotVerified()
    {
        RunVerificationRecord readable = RunVerificationRecord.Pin(Contract(), Sealed);
        RunVerificationRecord unreadable = readable.WithUnreadableEvidence(
            "the session store returned 3 malformed lines, so the check list is a guess");

        await Assert.That(unreadable.Verification.Verdict).IsNull().Because(
            "a verdict read off evidence that could not be read is a fabricated fact, and a fabricated "
            + "fact is the entire class of defect this slice exists to stop. The type makes it "
            + "unrepresentable: there is no third value that means 'probably not verified'.");
        await Assert.That(unreadable.Verification.IsUnknown).IsTrue();
        await Assert.That(unreadable.Verification.IsVerified).IsFalse();

        // THE DISCRIMINATION. Readable-but-empty and unreadable are two
        // different facts, and they must not print the same sentence.
        await Assert.That(unreadable.Summary).IsNotEqualTo(readable.Summary);
        await Assert.That(unreadable.Summary.Contains("unknown", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(readable.Summary.Contains("unknown", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(unreadable.NotVerified.Count()).IsEqualTo(1);
        await Assert.That(readable.NotVerified).IsEmpty();
    }

    [Test]
    public async Task UnreadableEvidence_WinsOverAPassThatWasActuallyRecorded()
    {
        // The direction that matters: unreadable evidence does not become
        // NotVerified just because a pass happens to be in the list, and it does
        // not become Verified either. The verdict is withdrawn.
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithCheck(Pass())
            .WithUnreadableEvidence("the read-back was garbled");

        await Assert.That(record.Verification.Verdict).IsNull();
        await Assert.That(record.Verification.IsVerified).IsFalse();
    }

    // ---- the four states print four different sentences ----

    [Test]
    public async Task Summary_GivesEveryStateItsOwnSentence()
    {
        // Four states, four strings. A single collapsed rendering — the
        // `Unknown => NotVerified` arm, or a switch whose two arms share a case
        // label — makes this fail on the count before the content is read.
        string verified = RunVerificationRecord.Pin(Contract(), Sealed).WithCheck(Pass()).Summary;
        string failed = RunVerificationRecord.Pin(Contract(), Sealed).WithCheck(Fail()).Summary;
        string notVerified = RunVerificationRecord.Pin(Contract(), Sealed).Summary;
        string unknown = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithUnreadableEvidence("garbled read-back").Summary;

        var rendered = new[] { verified, failed, notVerified, unknown };
        int distinct = rendered.Distinct(StringComparer.Ordinal).Count();

        await Assert.That(distinct).IsEqualTo(4).Because(
            "Verified / VerificationFailed / NotVerified / unknown are four different facts about a run. "
            + "If two of them render to one line, the reader is being told something the record does not "
            + "know — which is #782's plausible $0.00 and #711's two-meanings-in-one-field, at the display "
            + "boundary this line is. Rendered: " + string.Join(" || ", rendered));
    }

    [Test]
    public async Task Summary_CountsTheEvidenceBehindTheVerdict()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed)
            .WithCheck(Pass())
            .WithCheck(Pass())
            .WithNotVerified("lint", "eslint is not installed");

        await Assert.That(record.Summary.Contains("checks=2", StringComparison.Ordinal)).IsTrue();
        await Assert.That(record.Summary.Contains("not-verified=1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(record.Summary.Contains($"rev={Pinned}", StringComparison.Ordinal)).IsTrue().Because(
            "the revision belongs on the line a human reads. A verification claim with no commit on it is "
            + "the claim that goes stale quietly: nothing on the page says which tree it was about.");
    }

    // ---- the pin is the only source of the base revision ----

    [Test]
    public async Task BaseRevision_IsThePinnedContractRevision_ByteForByte()
    {
        WorkspaceContract contract = Contract(Pinned);
        RunVerificationRecord record = RunVerificationRecord.Pin(contract, Sealed);

        await Assert.That(record.BaseRevision).IsEqualTo(contract.BaseRevision);
        await Assert.That(record.Contract).IsEqualTo(contract);

        // A revision that is a prefix, a different case or a shortened sha is a
        // DIFFERENT string and must not be quietly accepted as the same pin.
        RunVerificationRecord other = RunVerificationRecord.Pin(Contract(Pinned[..12]), Sealed);
        await Assert.That(other.BaseRevision).IsNotEqualTo(record.BaseRevision);
        await Assert.That(other.WithCheck(RunCheck.Passed("build", 0, Pinned, Env, Took))
            .Verification.Verdict).IsEqualTo(RunVerificationVerdict.NotVerified);
    }

    [Test]
    public async Task TheRecord_CannotBeBuiltWithAnUnpinnedRevision()
    {
        // The base revision is a projection of Contract, so there is no
        // constructor parameter, no init setter and no way to set it. This test
        // is the compile-time half of that claim made executable: it pins the
        // shape from the outside, and ContractsArtifactCapabilityRule pins the
        // layer boundary the shape depends on.
        System.Reflection.PropertyInfo? baseRevision =
            typeof(RunVerificationRecord).GetProperty(nameof(RunVerificationRecord.BaseRevision));

        await Assert.That(baseRevision).IsNotNull();
        await Assert.That(baseRevision!.CanRead).IsTrue();
        await Assert.That(baseRevision.CanWrite).IsFalse().Because(
            "a settable BaseRevision is a second source of truth for the pin, and the slice's own AC "
            + "requires the record to reference the contract rather than grow its own. With a setter, a "
            + "caller could stamp any commit onto any run and the record would be a forgery with no "
            + "trace.");

        System.Reflection.ConstructorInfo[] publicCtors = typeof(RunVerificationRecord)
            .GetConstructors(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);

        await Assert.That(publicCtors).IsEmpty().Because(
            "every instance has to go through Pin and the With* methods, which is what validates. A public "
            + "constructor taking a revision string would let a caller skip all of it.");
    }

    [Test]
    public async Task RecordedAt_IsTheMomentTheEvidenceWasSealed_NotAReconstruction()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed);

        await Assert.That(record.RecordedAt).IsEqualTo(Sealed).Because(
            "the record has to be ageable. A run sealed at T whose record carries no T cannot be compared "
            + "against a later tree, which is how #593's \"verified\" strings end up describing code that "
            + "moved a month ago. The value is supplied by the caller at the seal point, never defaulted "
            + "to UtcNow at read time — that fabrication is #993's second defect, and it is not repeated "
            + "here.");
    }

    // ---- artifacts ----

    [Test]
    public async Task WithArtifact_RejectsAPathThatIsNotThere()
    {
        RunVerificationRecord record = RunVerificationRecord.Pin(Contract(), Sealed);
        string missing = Path.Combine(Path.GetTempPath(), $"harbor-405-absent-{Guid.NewGuid():N}.trx");

        Result<RunArtifact> created = RunArtifact.Create(missing, RunArtifactKind.Report);
        await Assert.That(created.IsFailure).IsTrue();
        await Assert.That(created.Error).Contains("does not exist", StringComparison.Ordinal);

        // The rejection has to be UNAVOIDABLE, not merely offered. A public
        // RunArtifact(path, kind) constructor would hand out the same value with
        // the check skipped, and the record would accept it — the artifact rule
        // would then be a suggestion that WithArtifact happened to decline to
        // use, which is not a rule.
        System.Reflection.ConstructorInfo[] publicCtors = typeof(RunArtifact)
            .GetConstructors(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);

        await Assert.That(publicCtors).IsEmpty().Because(
            "Create is the only way to obtain a RunArtifact, and Create is the only place the existence "
            + "check lives. A public constructor turns the slice's artifact rule into a convention.");
    }

    [Test]
    public async Task WithArtifact_RejectsABlankPath()
    {
        await Assert.That(RunArtifact.Create("  ", RunArtifactKind.Log).IsFailure).IsTrue();
    }

    [Test]
    public async Task WithArtifact_KeepsARealFile_AndTheRecordStaysVerifiable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-405-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "3 passed, 0 failed\n");
        try
        {
            var created = RunArtifact.Create(path, RunArtifactKind.Log);
            await Assert.That(created.IsSuccess).IsTrue();

            var record = RunVerificationRecord.Pin(Contract(), Sealed)
                .WithCheck(Pass())
                .WithArtifact(created.Value);

            await Assert.That(record.IsSuccess).IsTrue();
            await Assert.That(record.Value.Artifacts.Count()).IsEqualTo(1);
            await Assert.That(record.Value.Artifacts[0].Kind).IsEqualTo(RunArtifactKind.Log);
            await Assert.That(record.Value.Verification.Verdict).IsEqualTo(RunVerificationVerdict.Verified);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the two states that must never be reachable by accident ----

    [Test]
    public async Task The_Zero_Value_Of_RunVerification_Reads_As_Unknown_Not_As_NotVerified()
    {
        // A struct's default is what an uninitialised field holds and what a
        // `new RunVerification[0]`-style default-initialised array element is. If
        // it read as a definite "not verified" it would be #782 in its purest
        // form: a plausible negative standing in for the absence of a reading.
        RunVerification zero = default;

        await Assert.That(zero.Verdict).IsNull();
        await Assert.That(zero.IsVerified).IsFalse();
        await Assert.That(zero.IsUnknown).IsTrue().Because(
            "there is no verdict here at all, and that is what it has to say. default(RunVerification) "
            + "asserting NotVerified would let a forgotten initialisation look like a completed "
            + "inspection.");
        await Assert.That(zero.Describe().Contains("unknown", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [Test]
    public async Task AnUnnamedVerdictValue_ReadsAsUnknown_RatherThanAsOneOfTheThree()
    {
        // CS8524 made this arm mandatory at compile time, and the arm's SEMANTICS
        // are the point: a verdict value nobody can name — reachable through a
        // cast, a deserialiser, or a member added by a different change — is not
        // evidence of anything. Defaulting it to `NotVerified` would invent a
        // completed inspection; defaulting it to `Verified` would invent a pass.
        var unnamed = new RunVerification((RunVerificationVerdict)99, RunEvidenceState.Complete);

        await Assert.That(unnamed.IsVerified).IsFalse();
        await Assert.That(unnamed.Describe().Contains("unknown", StringComparison.OrdinalIgnoreCase)).IsTrue();

        string[] rendered =
        [
            .. new RunVerification?[]
            {
                new(RunVerificationVerdict.Verified, RunEvidenceState.Complete),
                new(RunVerificationVerdict.VerificationFailed, RunEvidenceState.Complete),
                new(RunVerificationVerdict.NotVerified, RunEvidenceState.Complete),
                null,
                unnamed,
            }.Select(v => v?.Describe() ?? new RunVerification(null, RunEvidenceState.Complete).Describe()),
        ];

        int distinct = rendered.Distinct(StringComparer.Ordinal).Count();

        await Assert.That(distinct).IsEqualTo(4).Because(
            "the five inputs are three verdicts, an absent verdict and an unnamed one — and they render "
            + "to four sentences, because the two forms of 'no verdict we can name' are the SAME fact and "
            + "must read the same. If the unnamed value got a sentence of its own, the record would be "
            + "inventing a fifth state that means nothing. Rendered: " + string.Join(" || ", rendered));
    }

    [Test]
    public async Task ACoverAllVerdict_DoesNotExist()
    {
        // #993's catch-all, one level up. A fourth member reachable by default
        // is how a future caller gets a verdict for a case nobody decided, and
        // a plausible one at that. The census is by reflection so it ages with
        // the type rather than with a list someone typed.
        string[] members = Enum.GetNames<RunVerificationVerdict>();
        await Assert.That(members).IsEquivalentTo(
            [nameof(RunVerificationVerdict.NotVerified), nameof(RunVerificationVerdict.Verified), nameof(RunVerificationVerdict.VerificationFailed)]).Because(
            "three verdicts, no default. A member added for the \"no evidence at all\" case is the "
            + "Unknown/NotVerified collapse wearing a new name, and this test is where it has to be "
            + "argued for rather than slipped in. Found: " + string.Join(" | ", members));

        string[] knowledge = Enum.GetNames<RunEvidenceState>();
        await Assert.That(knowledge).IsEquivalentTo(
            [nameof(RunEvidenceState.Complete), nameof(RunEvidenceState.Unreadable)]).Because(
            "the epistemic axis is exactly two states — we could read the evidence or we could not. A "
            + "third would be a verdict in disguise. Found: " + string.Join(" | ", knowledge));

        // And the two axes are separate types, which is what makes the collapse
        // a deliberate act rather than an overload of one field.
        await Assert.That(typeof(RunVerificationVerdict)).IsNotEqualTo(typeof(RunEvidenceState));
    }
}
