using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     #49 PR1: single cancellation ingress + decision linearization.
///     Every waiter completes (no hangs); exactly one outcome wins per gate.
/// </summary>
public class ApprovalCoordinatorTests
{
    private sealed class FakeRunner : IAgentRunner
    {
        public CancellationTokenSource AbortSource { get; } = new();
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
    }

    private static ApprovalCoordinator NewCoordinator() => new(NullLogger<ApprovalCoordinator>.Instance);

    private static ApprovalResolution Approve() => new(Approved: true, PersistDecision: false);

    [Test]
    public async Task DecideBeforeWait_WaiterGetsDecision()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1");

        await Assert.That(coordinator.DecideApproval("g1", Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.Accepted);
        var outcome = await coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task RegisterGate_IsIdempotent()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1");
        coordinator.RegisterGate("g1");

        await Assert.That(coordinator.DecideApproval("g1", Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.Accepted);
    }

    [Test]
    public async Task DoubleDecide_SecondIsAlreadyDecided()
    {
        var coordinator = NewCoordinator();
        coordinator.RegisterGate("g1");
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);

        await Assert.That(coordinator.DecideApproval("g1", Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.Accepted);
        await Assert.That(coordinator.DecideApproval("g1", new ApprovalResolution(false, false)))
            .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);

        // First decision stands.
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task DecideUnknownGate_IsStaleGate()
    {
        var coordinator = NewCoordinator();
        await Assert.That(coordinator.DecideApproval("nope", Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);
    }

    [Test]
    public async Task RequestCancel_UnblocksWaiterFailClosed_AndCancelsSource()
    {
        var coordinator = NewCoordinator();
        var runner = new FakeRunner();
        coordinator.RegisterGate("g1");
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);

        coordinator.RequestCancel(runner);

        var outcome = await wait;
        await Assert.That(outcome).IsNull(); // fail closed → asker maps to Deny
        await Assert.That(runner.AbortSource.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task DecideAfterCancel_IsAlreadyCancelled()
    {
        var coordinator = NewCoordinator();
        var runner = new FakeRunner();
        coordinator.RegisterGate("g1");
        coordinator.RequestCancel(runner);

        await Assert.That(coordinator.DecideApproval("g1", Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.AlreadyCancelled);

        var outcome = await coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
        await Assert.That(outcome).IsNull();
    }

    [Test]
    public async Task DecisionBeforeCancel_KeepsWin_ButRunStillAborts()
    {
        var coordinator = NewCoordinator();
        var runner = new FakeRunner();
        coordinator.RegisterGate("g1");
        var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);

        await Assert.That(coordinator.DecideApproval("g1", Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.Accepted);
        coordinator.RequestCancel(runner);

        // Decision won the race: waiter gets it. Cancel still aborts the run —
        // the tool executes with a cancelled token (existing ToolDispatcher path).
        var outcome = await wait;
        await Assert.That(outcome!.Approved).IsTrue();
        await Assert.That(runner.AbortSource.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RequestCancel_WhenIdleAndTwice_IsSafe()
    {
        var coordinator = NewCoordinator();
        var runner = new FakeRunner();

        coordinator.RequestCancel(runner);
        coordinator.RequestCancel(runner);

        await Assert.That(runner.AbortSource.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task ConcurrentDecideCancel_ExactlyOneWinner_AndSourceCancelled()
    {
        // Stress the lock ordering: every iteration exactly one outcome wins,
        // the waiter always completes, the source always ends cancelled.
        for (int i = 0; i < 50; i++)
        {
            var coordinator = NewCoordinator();
            var runner = new FakeRunner();
            string gate = $"g{i}";
            coordinator.RegisterGate(gate);
            var wait = coordinator.WaitForDecisionAsync(gate, CancellationToken.None);

            var decideTask = Task.Run(() => coordinator.DecideApproval(gate, Approve()));
            var cancelTask = Task.Run(() => coordinator.RequestCancel(runner));
            await Task.WhenAll(decideTask, cancelTask);

            var outcome = await wait;
            if (decideTask.Result == ApprovalDecisionDisposition.Accepted)
            {
                await Assert.That(outcome).IsNotNull();
            }
            else
            {
                await Assert.That(outcome).IsNull();
            }

            await Assert.That(runner.AbortSource.IsCancellationRequested).IsTrue();
        }
    }
}
