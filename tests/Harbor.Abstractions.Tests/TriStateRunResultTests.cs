// TriStateRunResultTests.cs — epic #41 slice B1.2 (#399).
//
// WHAT IS BEING CLAIMED HERE
// --------------------------
// A run result has three independent axes, and this slice adds the missing one:
//
//   execution    RunStopReason      how the loop exited   (pre-existing, untouched)
//   verification RunVerification    did the checks back it (B3.1 / #1018, untouched)
//   acceptance   RunAcceptance      did the user want it  (NEW, additive)
//
// The discriminating case is { Succeeded, NotVerified, Rejected }: work done,
// checks never run, user says no. A flat enum cannot express it, so these tests
// build it and then flip each axis in isolation to prove the other two hold.
//
// The old path is asserted, not assumed: every call below that omits acceptance
// uses the exact signature that existed before this slice, and the ten-argument
// constructor is constructed by hand. Both must keep compiling and keep their
// old meaning.

using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.TestKit;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Epic #41 slice B1.2: execution × verification × acceptance, and the
///     compatibility of the pre-existing execution path.
/// </summary>
public class TriStateRunResultTests
{
    private const string SessionId = "s-tristate";
    private const string Pinned = "1111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222";
    private const string Env = "linux-10.0/net10.0";
    private static readonly DateTimeOffset SealedAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Took = TimeSpan.FromSeconds(3);

    private static WorkspaceContract Contract() => new(
        RepoRoot: "/proj",
        BaseRevision: Pinned,
        BaseBranch: "dev",
        TrackedDirt: [],
        UntrackedPresent: false,
        Isolation: WorkspaceIsolation.Copy,
        Limits: new WorkspaceLimits());

    private static RunVerificationRecord EmptyVerification() =>
        RunVerificationRecord.Pin(Contract(), SealedAt);

    private static RunVerificationRecord PassedVerification() =>
        EmptyVerification().WithCheck(RunCheck.Passed("checks", 0, Pinned, Env, Took));

    private static AssistantMessage Final(StopReason reason = StopReason.Stop) => new(
        $"a-{Guid.NewGuid():N}",
        SessionId,
        SealedAt,
        new ContentPart[] { new TextPart("done") },
        reason,
        new Usage(1, 1),
        "model");

    private static UserMessage Prompt() => new(
        $"u-{Guid.NewGuid():N}", SessionId, SealedAt, "do work", "code", "model");

    private static RunOutcome Reconstruct(
        IReadOnlyList<AgentMessage> messages,
        RunVerificationRecord? verification = null) =>
        RunOutcome.Reconstruct(
            RunId.New(), SessionId, messages, TestEvents.AgentEnd(messages), verification: verification);

    // ---- the three axes are independent -------------------------------------

    [Test]
    public async Task Succeeded_NotVerified_Rejected_IsOneRepresentableOutcome()
    {
        // The state a flat stop-reason enum cannot express: the loop exited
        // cleanly, nobody ran a check, and the user does not want the change.
        AgentMessage[] messages = [Prompt(), Final()];
        RunVerificationRecord noChecks = EmptyVerification();

        RunOutcome outcome = RunOutcome.Reconstruct(
            RunId.New(), SessionId, messages, TestEvents.AgentEnd(messages),
            verification: noChecks) with { Acceptance = RunAcceptance.Rejected };

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Succeeded);
        await Assert.That(outcome.Verification!.Verification.Verdict)
            .IsEqualTo(RunVerificationVerdict.NotVerified);
        await Assert.That(outcome.Acceptance).IsEqualTo(RunAcceptance.Rejected);
    }

    [Test]
    public async Task FlippingOneAxis_LeavesTheOtherTwoUnchanged()
    {
        AgentMessage[] messages = [Prompt(), Final()];
        RunVerificationRecord passed = PassedVerification();

        RunOutcome baseline = RunOutcome.Reconstruct(
            RunId.New(), SessionId, messages, TestEvents.AgentEnd(messages),
            verification: passed) with { Acceptance = RunAcceptance.Pending };

        // Acceptance flips alone.
        RunOutcome rejected = baseline with { Acceptance = RunAcceptance.Rejected };
        await Assert.That(rejected.Acceptance).IsEqualTo(RunAcceptance.Rejected);
        await Assert.That(rejected.StopReason).IsEqualTo(baseline.StopReason);
        await Assert.That(rejected.Verification).IsSameReferenceAs(baseline.Verification);

        // Verification flips alone.
        RunOutcome unverified = baseline with { Verification = EmptyVerification() };
        await Assert.That(unverified.Verification!.Verification.Verdict)
            .IsEqualTo(RunVerificationVerdict.NotVerified);
        await Assert.That(unverified.StopReason).IsEqualTo(baseline.StopReason);
        await Assert.That(unverified.Acceptance).IsEqualTo(baseline.Acceptance);

        // Execution flips alone, with the other two carried across verbatim.
        RunOutcome failed = baseline with { StopReason = RunStopReason.Failed, ErrorMessage = "provider died" };
        await Assert.That(failed.StopReason).IsEqualTo(RunStopReason.Failed);
        await Assert.That(failed.Verification).IsSameReferenceAs(baseline.Verification);
        await Assert.That(failed.Acceptance).IsEqualTo(baseline.Acceptance);

        // No cross-derivation: a failed loop and an accepted change can coexist.
        await Assert.That(failed.Acceptance).IsEqualTo(RunAcceptance.Pending);
    }

    // ---- verification requires evidence, and has four honest states ---------

    [Test]
    public async Task NothingWasRun_IsNotVerified_AndNeverCollapsesIntoFailed()
    {
        RunVerificationRecord record = EmptyVerification();

        await Assert.That(record.Verification.Verdict).IsEqualTo(RunVerificationVerdict.NotVerified);
        await Assert.That(record.Verification.IsVerified).IsFalse();
        await Assert.That(record.Checks).IsEmpty();
    }

    [Test]
    public async Task OnePassed_OneFailed_AndBothTogether_GetTheirOwnVerdicts()
    {
        RunVerificationRecord passed = EmptyVerification().WithCheck(
            RunCheck.Passed("checks", 0, Pinned, Env, Took));
        RunVerificationRecord failed = EmptyVerification().WithCheck(
            RunCheck.Failed("checks", 1, Pinned, Env, Took));
        RunVerificationRecord both = passed.WithCheck(
            RunCheck.Failed("another check", 2, Pinned, Env, Took));

        await Assert.That(passed.Verification.Verdict).IsEqualTo(RunVerificationVerdict.Verified);
        await Assert.That(failed.Verification.Verdict).IsEqualTo(RunVerificationVerdict.VerificationFailed);
        await Assert.That(both.Verification.Verdict).IsEqualTo(RunVerificationVerdict.VerificationFailed)
            .Because("one red check outranks every green one; 'verified' with a failing check is a lie");

        // All three verdicts are distinct values, not aliases.
        await Assert.That(passed.Verification.Verdict).IsNotEqualTo(failed.Verification.Verdict);
        await Assert.That(failed.Verification.Verdict).IsNotEqualTo(RunVerificationVerdict.NotVerified);
    }

    [Test]
    public async Task Verified_IsUnreachableWithoutAPassingEvidenceItem()
    {
        // Evidence supplied but none of it passes: no verdict may flatter the run.
        RunVerificationRecord didNotRun = EmptyVerification().WithCheck(
            RunCheck.DidNotRun("checks", "tool missing", Pinned, Env, TimeSpan.Zero));
        RunVerificationRecord wrongRevision = EmptyVerification().WithCheck(
            RunCheck.Passed("checks", 0, Other, Env, Took));

        await Assert.That(didNotRun.Verification.IsVerified).IsFalse();
        await Assert.That(didNotRun.Verification.Verdict).IsEqualTo(RunVerificationVerdict.NotVerified);
        await Assert.That(wrongRevision.Verification.IsVerified).IsFalse()
            .Because("a pass on another revision says nothing about the pinned tree");

        // A failed item alongside a pass withdraws 'verified'.
        RunVerificationRecord mixed = EmptyVerification()
            .WithCheck(RunCheck.Passed("checks", 0, Pinned, Env, Took))
            .WithCheck(RunCheck.Failed("gate", 1, Pinned, Env, Took));
        await Assert.That(mixed.Verification.IsVerified).IsFalse();
    }

    [Test]
    public async Task Unknown_IsDistinctFromNotVerified()
    {
        RunVerification notVerified = new(RunVerificationVerdict.NotVerified, RunEvidenceState.Complete);
        RunVerification unknown = new(null, RunEvidenceState.Unreadable);

        await Assert.That(unknown.IsUnknown).IsTrue();
        await Assert.That(notVerified.IsUnknown).IsFalse();
        await Assert.That(unknown).IsNotEqualTo(notVerified);
        await Assert.That(unknown.Describe()).IsNotEqualTo(notVerified.Describe());

        // The zero value of the struct is on the 'unknown' side, never the
        // 'we looked and found nothing' side.
        await Assert.That(default(RunVerification).IsUnknown).IsTrue();
    }

    // ---- acceptance defaults honestly, and the old path still works ---------

    [Test]
    public async Task Acceptance_DefaultsToNotOffered_ForARunThatProducedNoChange()
    {
        // Legacy ten-argument construction: no acceptance argument exists there.
        var legacy = new RunOutcome(
            RunId.New(), SessionId, RunStopReason.Succeeded,
            SealedAt, SealedAt, [], [], null, null, null);
        await Assert.That(legacy.Acceptance).IsEqualTo(RunAcceptance.NotOffered);

        // Legacy Reconstruct: same signature as before B1.2. A terminal
        // assistant message is needed for the execution axis to read Succeeded
        // (#993); acceptance is still unsupplied.
        AgentMessage[] messages = [Prompt(), Final()];
        RunOutcome outcome = Reconstruct(messages);
        await Assert.That(outcome.Acceptance).IsEqualTo(RunAcceptance.NotOffered);
        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Succeeded);

        // An explicit decision is additive, not a rewrite of the old result.
        RunOutcome decided = Reconstruct(messages) with { Acceptance = RunAcceptance.Accepted };
        await Assert.That(decided.Acceptance).IsEqualTo(RunAcceptance.Accepted);
        await Assert.That(decided.StopReason).IsEqualTo(outcome.StopReason);
    }

    [Test]
    public async Task LegacyStopReasonMapping_IsUnchangedByTheAcceptanceAxis()
    {
        RunId runId = RunId.New();

        // Clean exit, via the exact call the pre-B1.2 tests used.
        AgentMessage[] done = [Prompt(), Final()];
        RunOutcome succeeded = RunOutcome.Reconstruct(
            runId, SessionId, done, TestEvents.AgentEnd(done));
        await Assert.That(succeeded.StopReason).IsEqualTo(RunStopReason.Succeeded);
        await Assert.That(succeeded.Acceptance).IsEqualTo(RunAcceptance.NotOffered);

        // Cancellation is Stopped, never Failed.
        AgentMessage[] aborted = [Prompt(), Final(StopReason.Aborted)];
        RunOutcome stopped = RunOutcome.Reconstruct(
            runId, SessionId, aborted, TestEvents.AgentEnd(aborted, cancelled: true));
        await Assert.That(stopped.StopReason).IsEqualTo(RunStopReason.Stopped);

        // A known error still wins, and carries its message.
        RunOutcome failed = RunOutcome.Reconstruct(
            runId, SessionId, aborted, TestEvents.AgentEnd(aborted),
            TestEvents.AgentError("provider exploded"));
        await Assert.That(failed.StopReason).IsEqualTo(RunStopReason.Failed);
        await Assert.That(failed.ErrorMessage).IsEqualTo("provider exploded");

        // #993: an empty history is not a clean exit.
        RunOutcome empty = RunOutcome.Reconstruct(
            runId, SessionId, [], TestEvents.AgentEnd([]));
        await Assert.That(empty.StopReason).IsEqualTo(RunStopReason.Stopped);
        await Assert.That(empty.StartedAt).IsNull();
    }

    [Test]
    public async Task AcceptanceEnum_HasExactlyTheFourDocumentedMembers()
    {
        string[] members = Enum.GetNames<RunAcceptance>();
        await Assert.That(members).IsEquivalentTo(
            [nameof(RunAcceptance.Pending), nameof(RunAcceptance.Accepted),
             nameof(RunAcceptance.Rejected), nameof(RunAcceptance.NotOffered)]).Because(
            "four members, no catch-all. 'Unknown' is not a fifth acceptance verdict: "
            + "it is the absence of a decision, which the verification axis models "
            + "on its own epistemic axis and which acceptance records as Pending. "
            + "Found: " + string.Join(" | ", members));
    }
}
