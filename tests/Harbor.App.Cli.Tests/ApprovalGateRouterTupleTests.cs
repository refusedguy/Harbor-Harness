using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.App.Cli.Repl;
using Harbor.Application.Permissions;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Events;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #49 PR4 follow-up (router migration): the approval gate carries the full
///     4-tuple. The asker binds <c>(gateId, invocationId, generation)</c> from
///     the <see cref="PermissionRequest" />; the router stamps the same tuple
///     from the gate view. A stale view answering with the wrong identity is
///     <see cref="ApprovalDecisionDisposition.StaleGate" /> and touches nothing
///     (fail closed) — the genuine key still lands afterwards.
///     UI stub: keys are driven programmatically, no terminal involved.
/// </summary>
public class ApprovalGateRouterTupleTests
{
    private static ApprovalCoordinator NewCoordinator() => new(NullLogger<ApprovalCoordinator>.Instance);

    private static (ApprovalGateRouter Router, ChatTimelinePanel Panel) MakeRouter(IApprovalCoordinator coordinator)
    {
        var panel = new ChatTimelinePanel("chat", 40, 6);
        return (new ApprovalGateRouter(panel, new StatusViewModel()) { Coordinator = coordinator }, panel);
    }

    private static ApprovalResolution Approve() => new(Approved: true, PersistDecision: false);

    private static ApprovalResolution Deny() => new(Approved: false, PersistDecision: false);

    [Test]
    public async Task BoundMatch_Accepted_WaiterResolves()
    {
        var coordinator = NewCoordinator();
        var (router, _) = MakeRouter(coordinator);
        var gate = router.BeginApprovalGate("bash", "rm -rf /", "inv-1", 1);
        coordinator.RegisterGate(gate.Id, "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);

        await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('y')))).IsTrue();

        var outcome = await wait;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task BoundMatch_LateTwin_IsAlreadyDecided_AndDoesNotOverwrite()
    {
        // #797: this is what the previous BoundMatch_Accepted_WaiterResolves asserted
        // with a waiter parked on the very TCS the router just completed — a
        // disposition the test did not own. WaitForDecisionAsync drops the slot on
        // consume (ApprovalCoordinator.cs:227), and the continuation is queued by
        // RunContinuationsAsynchronously, so who runs first was up to the scheduler:
        // test thread first → AlreadyDecided, waiter first → the slot is gone → StaleGate.
        // Both are correct product states; only the first was the one asserted.
        //
        // No waiter is started until the twin is stamped, so the gate is provably
        // still registered and the disposition follows slot.Decided, not scheduling.
        var coordinator = NewCoordinator();
        var (router, _) = MakeRouter(coordinator);
        var gate = router.BeginApprovalGate("bash", "rm -rf /", "inv-1", 1);
        coordinator.RegisterGate(gate.Id, "inv-1", 1);

        // The router's stamp wins: gate registered, tuple bound, nothing decided yet.
        await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('y')))).IsTrue();

        // A late twin with the winning identity cannot accept twice.
        await Assert.That(coordinator.DecideApproval(gate.Id, "inv-1", 1, Deny()))
            .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);

        // And the winner stands: the twin's Deny did not overwrite the slot, so a
        // waiter attaching afterwards resolves the approval, not the denial.
        var outcome = await coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task StaleReplay_Rejected_GenuineKeyStillLands()
    {
        var coordinator = NewCoordinator();
        var (router, _) = MakeRouter(coordinator);
        var gate = router.BeginApprovalGate("bash", "rm -rf /", "inv-1", 1);
        coordinator.RegisterGate(gate.Id, "inv-1", 1);
        var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);

        // A stale view (foreign invocation, future generation) is dropped, gate untouched.
        await Assert.That(coordinator.DecideApproval(gate.Id, "inv-2", 1, Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);
        await Assert.That(coordinator.DecideApproval(gate.Id, "inv-1", 2, Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.StaleGate);

        await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('y')))).IsTrue();
        var outcome = await wait;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task RouterStampsFullTuple_MismatchedViewBinding_TouchesNothing()
    {
        // Proves the router sends the 4-tuple, not the gate-only path: the view
        // binding disagrees with the coordinator binding, so the stamp is
        // rejected as StaleGate and the genuine decision still lands after.
        // (A legacy 2-arg stamp would have been Accepted here.)
        var coordinator = NewCoordinator();
        var (router, _) = MakeRouter(coordinator);
        var gate = router.BeginApprovalGate("bash", "rm -rf /", "inv-view", 1);
        coordinator.RegisterGate(gate.Id, "inv-coord", 1);
        var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);

        await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('y')))).IsTrue();

        await Assert.That(coordinator.DecideApproval(gate.Id, "inv-coord", 1, Approve()))
            .IsEqualTo(ApprovalDecisionDisposition.Accepted);
        var outcome = await wait;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsTrue();
    }

    [Test]
    public async Task LegacyUnboundGate_FallsBackToGateOnlyPath()
    {
        // Tool-built asks (ToolContext.Ask) carry no invocation: the asker
        // registers an unbound gate and the router keeps the gate-only stamp.
        var coordinator = NewCoordinator();
        var (router, _) = MakeRouter(coordinator);
        var gate = router.BeginApprovalGate("bash", "rm -rf /");
        coordinator.RegisterGate(gate.Id);
        var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);

        await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('n')))).IsTrue();

        var outcome = await wait;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Approved).IsFalse();
    }

    [Test]
    public async Task BoundAsk_EndToEnd_MapsToAllow()
    {
        // Asker plumb: InvocationId/Generation from the request bind the gate;
        // the router stamps the same tuple (an unbound registration here would
        // leave the 4-tuple stamp StaleGate and the ask would never complete).
        var coordinator = NewCoordinator();
        var panel = new ChatTimelinePanel("chat", 40, 6);
        using var bridge = new ChatScreenBridge(new InMemoryEventBus(), panel, new StatusViewModel(), autoSubscribe: false, coordinator: coordinator);
        var asker = new CellForgePermissionAsker(() => bridge, coordinator);

        var ask = asker.AskAsync(new PermissionRequest(
            "bash", "*", JsonDocument.Parse("{\"command\":\"cargo build\"}").RootElement.Clone(),
            ["allow", "deny"], "inv-7", 1), CancellationToken.None);
        bridge.Tick(0);
        await Assert.That(panel.Timeline.Count).IsEqualTo(1);

        await Assert.That(bridge.TryRouteApprovalKey(KeyEvent.Char(new System.Text.Rune('a')))).IsTrue();
        var response = await ask;
        await Assert.That(response.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(response.PersistDecision).IsTrue();
    }
}
