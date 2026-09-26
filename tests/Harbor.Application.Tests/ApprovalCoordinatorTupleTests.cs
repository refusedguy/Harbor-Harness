using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     #49 PR4: UI-sent full 4-tuple <c>(GateId, InvocationId, Generation, Choice)</c>.
///     The coordinator validates every component; any identity mismatch is
///     fail-closed (StaleGate, gate untouched, logged) — a wrong-identity
///     approve can never approve the wrong gate, and a replayed old generation
///     is denied the same way.
/// </summary>
public class ApprovalCoordinatorTupleTests
{
    private sealed class FakeRunner : IAgentRunner
    {
        public CancellationTokenSource AbortSource { get; } = new();
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
    }

    /// <summary>
    ///     Test stub for the UI sender side of the 4-tuple contract: holds the
    ///     (gate, invocation, generation) the UI received with the approval
    ///     request and sends decisions back through the coordinator — the shape
    ///     the router will use once it migrates off oldest-pending stamping.
    /// </summary>
    private sealed class UiApprovalSender(
        IApprovalCoordinator coordinator, string gateId, string invocationId, int generation)
    {
        public ApprovalDecisionDisposition Approve() => Send(invocationId, generation, approved: true);

        public ApprovalDecisionDisposition Deny() => Send(invocationId, generation, approved: false);

        public ApprovalDecisionDisposition Send(string sendInvocationId, int sendGeneration, bool approved) =>
            coordinator.DecideApproval(
                gateId, sendInvocationId, sendGeneration, new ApprovalResolution(approved, false));
    }

    private static ApprovalCoordinator NewCoordinator() => new(NullLogger<ApprovalCoordinator>.Instance);

    [Test]
    public async Task TupleHappyPath_Accepted_WaiterGetsDecision()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1", "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.Accepted);

        var outcome = await wait;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task TupleDeny_Accepted_WaiterGetsDeny()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1", "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        await Assert.That(ui.Deny()).IsEqualTo(ApprovalDecisionDisposition.Accepted);

        var outcome = await wait;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsFalse();
    }

    [Test]
    public async Task TupleUnknownGate_IsStaleGate()
    {
        var coordinator = NewCoordinator();
        var ui = new UiApprovalSender(coordinator, "nope", "inv-1", 1);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.StaleGate);
    }

    [Test]
    public async Task TupleInvocationMismatch_Rejected_GateUntouched()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1", "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        // A decision for a FOREIGN invocation: dropped, gate stays pending.
        await Assert.That(ui.Send("inv-2", 1, approved: true))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        // The genuine decision still lands afterwards — the mismatch touched nothing.
        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.Accepted);
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task TupleGenerationMismatch_Rejected_GateUntouched()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1", "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        // A future attempt answering this gate: dropped, gate stays pending.
        await Assert.That(ui.Send("inv-1", 2, approved: true))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.Accepted);
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task TupleReplayOldGeneration_Denied()
    {
        // Retried attempt re-asked at generation 2: the old generation's
        // approve replayed late must never decide the gate (fail closed).
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1", "inv-1", 2);
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 2);

        await Assert.That(ui.Send("inv-1", 1, approved: true))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        // Untrusted zero generation from the UI: same fail-closed path, no throw.
        await Assert.That(ui.Send("inv-1", 0, approved: true))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.Accepted);
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task TupleOnUnboundGate_IsStaleGate_LegacyDecideStillWorks()
    {
        // Legacy (PR1) registration carries no binding: a 4-tuple decision
        // cannot be verified, so it is rejected fail-closed. The legacy
        // gate-only path keeps its exact PR1 semantics for the not-yet-migrated router.
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1");
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        // Degenerate sender (null invocation, generation 0) is rejected too —
        // it must never match an unbound slot via Equals(null, null) + 0 == 0.
        await Assert.That(ui.Send(null!, 0, approved: true))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        await Assert.That(coordinator.DecideApproval("g1", new ApprovalResolution(Approved: true, PersistDecision: false)))
            .IsEqualTo(ApprovalDecisionDisposition.Accepted);
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task TupleAfterCancel_IsAlreadyCancelled()
    {
        var coordinator = NewCoordinator();
        var runner = new FakeRunner();
        coordinator.RegisterGate("g1", "inv-1", 1);
        coordinator.RequestCancel(runner);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.AlreadyCancelled);

        var outcome = await coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        await Assert.That(outcome).IsNull();
    }

    [Test]
    public async Task TupleDoubleDecide_SecondIsAlreadyDecided()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1", "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        var ui = new UiApprovalSender(coordinator, "g1", "inv-1", 1);

        await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.Accepted);
        await Assert.That(ui.Deny()).IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);

        // First decision stands.
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task TupleRegister_InvalidBinding_Throws()
    {
        var coordinator = NewCoordinator();

        await Assert.That(() => coordinator.RegisterGate("g1", "inv-1", 0))
            .Throws<ArgumentOutOfRangeException>();
    }
}
