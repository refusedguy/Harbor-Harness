using System.Collections.Concurrent;
using System.Collections.Frozen;
using Harbor.Abstractions.Permissions;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
///     Approval-gate routing behind the chat timeline (SRP extraction from
///     <see cref="ChatScreenBridge"/>): gate lifecycle (request/begin/drain),
///     key/click routing to the oldest pending gate, and diff-navigation
///     routing. All list mutation stays on the render thread — off-thread
///     requests land in a concurrent queue drained per tick.
/// </summary>
public sealed class ApprovalGateRouter(ChatTimelinePanel panel, StatusViewModel status)
{
    private const int MaxPendingGates = 8;

    private readonly Queue<ApprovalGateView> _pendingGates = new();

    /// <summary>
    ///     Runtime approval coordinator (#49 PR1). When set, every recorded
    ///     decision is stamped into the coordinator after the view applies it —
    ///     the coordinator is the single rendezvous for the asker waiter vs
    ///     cancellation. Null keeps the legacy view-only path (tests, hosts
    ///     without the coordinator registered).
    /// </summary>
    public IApprovalCoordinator? Coordinator { get; set; }

    /// <summary>Gates posted off the render thread (tool-execution context), drained by <see cref="DrainQueued" />.</summary>
    private readonly ConcurrentQueue<ApprovalGateView> _gateQueue = new();

    /// <summary>
    /// Optional TEA store (epic C contour): when set, diff-navigation steps
    /// dispatch scroll <see cref="AppMsg.KeyInput"/> through the store instead of
    /// executing the diff view-model commands directly — the reducer owns the
    /// meaning, every renderer shares one experience. Null keeps the legacy
    /// view-only path (tests, hosts without a composed store).
    /// </summary>
    public UiStore? Store { get; set; }

    /// <summary>
    /// Reject reasons attached to modal Deny commits, keyed by gate id
    /// ([UX7] #267 audit trail — the coordinator resolution carries no free
    /// text, so the host reads the reason here for the Deny stamp or the
    /// tool-result note). Bounded; oldest entries are evicted past the cap.
    /// </summary>
    private readonly Dictionary<string, string> _rejectReasons = new(StringComparer.Ordinal);

    private const int MaxRejectReasons = 32;

    /// <summary>
    /// Reject reason recorded for <paramref name="gateId"/> (empty when the
    /// gate was not denied through the modal path or carried no reason).
    /// </summary>
    public string GetRejectReason(string? gateId) =>
        gateId is not null && _rejectReasons.TryGetValue(gateId, out string? reason) ? reason : string.Empty;

    /// <summary>
    /// Thread-safe approval request for the agent-loop side of the seam:
    /// creates a gate the caller can await via <c>DecisionRecorded</c>, and
    /// enqueues it so the frame loop appends it onto the timeline on its next
    /// tick — all list mutation stays on the render thread.
    /// The PR4 identity rides on the gate view itself (created here off the
    /// render thread, read after the queue handoff — no shared map needed).
    /// </summary>
    public ApprovalGateView RequestApprovalGate(string toolName, string detail, string? invocationId = null, int generation = 1)
    {
        var gate = new ApprovalGateView(toolName, detail, invocationId, generation);
        _gateQueue.Enqueue(gate);
        status.SignalMascot(MascotReaction.ApprovalWiggle);
        return gate;
    }

    /// <summary>
    /// Appends a permission gate to the timeline and arms it at the tail of
    /// the pending queue. Every queued gate stays interactable in arrival
    /// order — the front one is answered first; deciding it exposes the next.
    /// </summary>
    public ApprovalGateView BeginApprovalGate(string toolName, string detail, string? invocationId = null, int generation = 1)
    {
        var gate = new ApprovalGateView(toolName, detail, invocationId, generation);
        panel.Timeline.Append(gate);
        EnqueuePendingGate(gate);
        panel.Timeline.MarkLastDirty();
        status.SignalMascot(MascotReaction.ApprovalWiggle);
        return gate;
    }

    /// <summary>Render-thread drain of gates requested off-thread; every queued
    /// gate lands on the timeline and joins the pending queue in arrival order.</summary>
    public void DrainQueued()
    {
        bool appended = false;
        while (_gateQueue.TryDequeue(out var gate))
        {
            panel.Timeline.Append(gate);
            EnqueuePendingGate(gate);
            appended = true;
        }

        if (appended)
        {
            panel.Timeline.MarkLastDirty();
        }
    }

    /// <summary>
    /// Routes one key event to the OLDEST pending gate BEFORE composer input.
    /// Consumed keys always wake the frame pipeline (decision stamps repaint).
    /// Returns false while no gate is armed or the key is not one of y/n/a/
    /// Enter/Escape — callers fall through to normal routing.
    /// </summary>
    public bool TryRouteApprovalKey(in KeyEvent key)
    {
        PruneResolvedGates();
        if (_pendingGates.Count == 0)
        {
            return false;
        }

        var gate = _pendingGates.Peek();
        if (!gate.HandleKey(key))
        {
            return false;
        }

        if (!gate.IsPending)
        {
            _ = _pendingGates.Dequeue();
            StampDecision(gate);
        }

        panel.Timeline.MarkLastDirty();
        return true;
    }

    /// <summary>
    /// Routes a left-button press/click to the OLDEST pending gate's hint-row
    /// buttons (see <see cref="Widgets.ApprovalGateView.TryHitDecision" />).
    /// Returns false when no gate is armed or the click lands outside its
    /// decision zones — callers keep normal scroll/routing behavior.
    /// </summary>
    public bool TryRouteApprovalClick(in Input.MouseEvent mouse)
    {
        PruneResolvedGates();
        if (_pendingGates.Count == 0)
        {
            return false;
        }

        var gate = _pendingGates.Peek();
        if (mouse.Type is not (Input.MouseEventType.Press or Input.MouseEventType.Click)
            || mouse.Button != Input.MouseButton.Left)
        {
            return false;
        }

        if (gate.TryHitDecision(mouse.Column, mouse.Row) is not { } choice
            || !gate.TryDecide(choice))
        {
            return false;
        }

        if (!gate.IsPending)
        {
            _ = _pendingGates.Dequeue();
            StampDecision(gate);
        }

        panel.Timeline.MarkLastDirty();
        return true;
    }

    /// <summary>
    /// Commits the [UX7] approval modal's choice to the OLDEST pending gate
    /// ([UX7] #267 — the host shows <c>DialogOverlay.ShowApproval</c>, routes
    /// keys through the dialog, and calls this when Enter commits; Escape or
    /// dismissal commits <see cref="ApprovalChoice.Deny"/>, fail closed).
    /// Session-allow rides the existing stamp (<c>AlwaysAllow</c> maps to a
    /// persisted decision); a Deny reason is retained for
    /// <see cref="GetRejectReason"/>. Returns false when no gate is pending.
    /// </summary>
    public bool CommitModalDecision(ApprovalChoice choice, string? rejectReason = null)
    {
        PruneResolvedGates();
        if (_pendingGates.Count == 0)
        {
            return false;
        }

        var gate = _pendingGates.Dequeue();
        ApprovalChoice effective = choice is ApprovalChoice.Approve or ApprovalChoice.AlwaysAllow
            ? choice
            : ApprovalChoice.Deny;
        if (!gate.TryDecide(effective))
        {
            return false;
        }

        // Only an explicit Deny carries a reason — fail-closed coercions
        // (None / unknown) stamp Deny without audit text.
        if (choice == ApprovalChoice.Deny && !string.IsNullOrWhiteSpace(rejectReason))
        {
            if (_rejectReasons.Count >= MaxRejectReasons)
            {
                _rejectReasons.Remove(_rejectReasons.Keys.First());
            }
            _rejectReasons[gate.Id] = rejectReason.Trim();
        }

        StampDecision(gate);
        panel.Timeline.MarkLastDirty();
        return true;
    }

    /// <summary>
    /// Steps through the diff preview: down/next on
    /// <see cref="ChatAction.ScrollDownLine"/>, up/previous on
    /// <see cref="ChatAction.ScrollUpLine"/>. With <see cref="Store"/> set the
    /// step travels as a store <see cref="AppMsg.KeyInput"/> (the diff
    /// view-model is untouched); otherwise it falls back to the legacy
    /// view-model commands. Other actions are ignored.
    /// A single <see cref="DiffNavigationStep"/> table owns the decision (#197) —
    /// a new navigable action adds one row, never a second switch.
    /// </summary>
    public void RouteDiffNavigation(DiffPreviewViewModel diffVm, ChatAction action)
    {
        if (!DiffNavigationSteps.TryGetValue(action, out var step))
        {
            return;
        }

        if (Store is { } store)
        {
            _ = store.Dispatch(step.ToStoreMessage());
            return;
        }

        if (diffVm is null) return;
        step.ApplyLegacy(diffVm);
    }

    /// <summary>
    ///     One diff-navigation row: the store message and the legacy
    ///     view-model command for the same <see cref="ChatAction" />.
    /// </summary>
    private sealed record DiffNavigationStep(
        Func<AppMsg> ToStoreMessage,
        Action<DiffPreviewViewModel> ApplyLegacy);

    private static readonly FrozenDictionary<ChatAction, DiffNavigationStep> DiffNavigationSteps =
        new Dictionary<ChatAction, DiffNavigationStep>
        {
            [ChatAction.ScrollDownLine] = new(
                () => new AppMsg.KeyInput(ChatAction.ScrollDownLine, new UiKey(UiKeyCode.Down)),
                diffVm => diffVm.NextDiffCommand.Execute(null)),
            [ChatAction.ScrollUpLine] = new(
                () => new AppMsg.KeyInput(ChatAction.ScrollUpLine, new UiKey(UiKeyCode.Up)),
                diffVm => diffVm.PreviousDiffCommand.Execute(null)),
        }.ToFrozenDictionary();

    /// <summary>Appends to the pending queue, auto-denying the oldest gate on
    /// overflow (the bound keeps both the queue and host-side waiters finite).</summary>
    private void EnqueuePendingGate(ApprovalGateView gate)
    {
        _pendingGates.Enqueue(gate);
        while (_pendingGates.Count > MaxPendingGates)
        {
            var dropped = _pendingGates.Dequeue();
            if (dropped.TryDecide(ApprovalChoice.Deny))
            {
                StampDecision(dropped);
            }
        }
    }

    /// <summary>
    ///     Records a view-applied decision in the coordinator (#49 PR1; PR4 4-tuple).
    ///     A bound gate stamps the full <c>(gateId, invocationId, generation)</c>
    ///     identity the asker registered — a stale view answering the wrong
    ///     attempt is rejected as <c>StaleGate</c> and touches nothing. A legacy
    ///     unbound gate keeps the gate-only path (a 4-tuple decide on an unbound
    ///     gate is <c>StaleGate</c> by contract, so the fallback is required —
    ///     not a second world: bound gates never take it).
    ///     Best-effort: the asker waiter is already unblocked by either the
    ///     decision or a raced cancel, so a non-Accepted disposition only
    ///     documents the race (cancel won first / duplicate stamp).
    /// </summary>
    private void StampDecision(ApprovalGateView gate)
    {
        var coordinator = Coordinator;
        if (coordinator is null)
        {
            return;
        }

        if (gate.InvocationId is null)
        {
            coordinator.DecideApproval(gate.Id, MapChoice(gate.Decision));
            return;
        }

        coordinator.DecideApproval(gate.Id, gate.InvocationId, gate.Generation, MapChoice(gate.Decision));
    }

    private static ApprovalResolution MapChoice(ApprovalChoice choice) => choice switch
    {
        ApprovalChoice.Approve => new ApprovalResolution(Approved: true, PersistDecision: false),
        ApprovalChoice.AlwaysAllow => new ApprovalResolution(Approved: true, PersistDecision: true),
        _ => new ApprovalResolution(Approved: false, PersistDecision: false),
    };

    /// <summary>Drops gates resolved off the routing path (e.g. host called
    /// <see cref="Widgets.ApprovalGateView.TryDecide" /> directly).</summary>
    private void PruneResolvedGates()
    {
        while (_pendingGates.Count > 0 && !_pendingGates.Peek().IsPending)
        {
            _ = _pendingGates.Dequeue();
        }
    }
}
