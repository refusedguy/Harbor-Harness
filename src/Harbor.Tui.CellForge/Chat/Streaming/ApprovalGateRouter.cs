using System.Collections.Concurrent;
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

    /// <summary>Gates posted off the render thread (tool-execution context), drained by <see cref="DrainQueued" />.</summary>
    private readonly ConcurrentQueue<ApprovalGateView> _gateQueue = new();

    /// <summary>
    /// Thread-safe approval request for the agent-loop side of the seam:
    /// creates a gate the caller can await via <c>DecisionRecorded</c>, and
    /// enqueues it so the frame loop appends it onto the timeline on its next
    /// tick — all list mutation stays on the render thread.
    /// </summary>
    public ApprovalGateView RequestApprovalGate(string toolName, string detail)
    {
        var gate = new ApprovalGateView(toolName, detail);
        _gateQueue.Enqueue(gate);
        status.SignalMascot(MascotReaction.ApprovalWiggle);
        return gate;
    }

    /// <summary>
    /// Appends a permission gate to the timeline and arms it at the tail of
    /// the pending queue. Every queued gate stays interactable in arrival
    /// order — the front one is answered first; deciding it exposes the next.
    /// </summary>
    public ApprovalGateView BeginApprovalGate(string toolName, string detail)
    {
        var gate = new ApprovalGateView(toolName, detail);
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
        }

        panel.Timeline.MarkLastDirty();
        return true;
    }

    public void RouteDiffNavigation(DiffPreviewViewModel diffVm, ChatAction action)
    {
        if (diffVm is null) return;
        switch (action)
        {
            case ChatAction.ScrollDownLine:
                diffVm.NextDiffCommand.Execute(null);
                break;
            case ChatAction.ScrollUpLine:
                diffVm.PreviousDiffCommand.Execute(null);
                break;
        }
    }

    /// <summary>Appends to the pending queue, auto-denying the oldest gate on
    /// overflow (the bound keeps both the queue and host-side waiters finite).</summary>
    private void EnqueuePendingGate(ApprovalGateView gate)
    {
        _pendingGates.Enqueue(gate);
        while (_pendingGates.Count > MaxPendingGates)
        {
            _ = _pendingGates.Dequeue().TryDecide(ApprovalChoice.Deny);
        }
    }

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
