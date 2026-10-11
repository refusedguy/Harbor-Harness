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
    /// Always-pattern allowlist ([steal/opencode] #1170, epic #1155):
    /// patterns persisted from <see cref="ApprovalChoice.AlwaysAllow"/>
    /// decisions (<c>"tool:*"</c> per tool). Shown at the always-stage
    /// (<c>permissionAlwaysLines</c> in opencode's permission.ts) — never a
    /// blind allow: the user picks a scoped pattern, not "everything".
    /// Bounded; oldest entries are evicted past the cap.
    /// </summary>
    public IReadOnlyList<string> AlwaysPatterns => _alwaysPatterns;

    private readonly List<string> _alwaysPatterns = new();

    private const int MaxAlwaysPatterns = 32;

    /// <summary>
    /// Per-gate 3-stage machines (<c>permission → always → reject</c>), keyed
    /// by gate id. Created lazily on first stage query; gates created before
    /// this field existed resolve to a fresh machine at
    /// <see cref="PermissionStage.Permission"/> (never null, never throws).
    /// </summary>
    private readonly Dictionary<string, PermissionMachine> _machines = new(StringComparer.Ordinal);

    /// <summary>
    /// Reject reason recorded for <paramref name="gateId"/> (empty when the
    /// gate was not denied through the modal path or carried no reason).
    /// </summary>
    public string GetRejectReason(string? gateId) =>
        gateId is not null && _rejectReasons.TryGetValue(gateId, out string? reason) ? reason : string.Empty;

    /// <summary>
    /// Reject-with-message as agent feedback ([steal/opencode] #1170 (b)):
    /// the Deny reason the user typed is returned verbatim so the host can
    /// send it back to the agent as a feedback message (opencode routes the
    /// reject text into the next turn's context). Empty when the gate was
    /// not denied with a message.
    /// </summary>
    public string GetRejectFeedback(string? gateId) => GetRejectReason(gateId);

    /// <summary>
    /// Streaming-input guard ([steal/opencode] #1170 (d)): permission info
    /// must be built from final tool args only — a partial delta mid-stream
    /// (half a command, half a path) would present a lie and, worse, an
    /// always-pattern scoped to a truncation. Streaming calls contribute an
    /// empty detail; the gate still queues so ordering is preserved.
    /// </summary>
    public static string SanitizeGateDetail(string? detail, bool isStreaming) =>
        isStreaming ? string.Empty : (detail ?? string.Empty).Trim();

    /// <summary>Current 3-stage position of <paramref name="gateId"/> (fresh gates sit at <see cref="PermissionStage.Permission"/>).</summary>
    public PermissionStage GateStage(string gateId) => MachineFor(gateId).Stage;

    /// <summary>
    /// Moves <paramref name="gateId"/> from permission to the always-stage
    /// (pattern list). Returns false when the gate already left permission
    /// (cancel first to come back).
    /// </summary>
    public bool RequestAlwaysStage(string gateId) => MachineFor(gateId).RequestAlways();

    /// <summary>
    /// Moves <paramref name="gateId"/> to the reject-stage (message field).
    /// Returns false when already there.
    /// </summary>
    public bool RequestRejectStage(string gateId) => MachineFor(gateId).RequestReject();

    /// <summary>Abandons the always-/reject-stage and returns to permission (opencode's cancel).</summary>
    public void CancelStage(string gateId) => MachineFor(gateId).Cancel();

    /// <summary>
    /// Always-stage pattern list for <paramref name="toolName"/>
    /// (<c>permissionAlwaysLines</c>): persisted patterns first, then the
    /// candidate for this gate (<c>"tool:*"</c>) when not already stored.
    /// The UI renders exactly this — an explicit scoped list, never a blind
    /// "always allow everything".
    /// </summary>
    public IReadOnlyList<string> PermissionAlwaysLines(string? toolName)
    {
        string candidate = AlwaysPatternFor(toolName);
        if (_alwaysPatterns.Contains(candidate))
        {
            return _alwaysPatterns.ToArray();
        }

        var lines = new List<string>(_alwaysPatterns.Count + 1);
        lines.AddRange(_alwaysPatterns);
        lines.Add(candidate);
        return lines;
    }

    private PermissionMachine MachineFor(string gateId)
    {
        if (string.IsNullOrEmpty(gateId))
        {
            return new PermissionMachine();
        }

        if (!_machines.TryGetValue(gateId, out var machine))
        {
            machine = new PermissionMachine();
            _machines[gateId] = machine;
        }

        return machine;
    }

    private static string AlwaysPatternFor(string? toolName) =>
        (string.IsNullOrWhiteSpace(toolName) ? "?" : toolName.Trim()) + ":*";

    private void RecordAlwaysPattern(string? toolName)
    {
        string pattern = AlwaysPatternFor(toolName);
        if (_alwaysPatterns.Contains(pattern))
        {
            return;
        }

        if (_alwaysPatterns.Count >= MaxAlwaysPatterns)
        {
            _alwaysPatterns.RemoveAt(0);
        }

        _alwaysPatterns.Add(pattern);
    }

    /// <summary>
    /// Thread-safe approval request for the agent-loop side of the seam:
    /// creates a gate the caller can await via <c>DecisionRecorded</c>, and
    /// enqueues it so the frame loop appends it onto the timeline on its next
    /// tick — all list mutation stays on the render thread.
    /// The PR4 identity rides on the gate view itself (created here off the
    /// render thread, read after the queue handoff — no shared map needed).
    /// </summary>
    public ApprovalGateView RequestApprovalGate(string toolName, string detail, string? invocationId = null, int generation = 1, bool isStreaming = false)
    {
        var gate = new ApprovalGateView(toolName, SanitizeGateDetail(detail, isStreaming), invocationId, generation);
        _gateQueue.Enqueue(gate);
        status.SignalMascot(MascotReaction.ApprovalWiggle);
        return gate;
    }

    /// <summary>
    /// Appends a permission gate to the timeline and arms it at the tail of
    /// the pending queue. Every queued gate stays interactable in arrival
    /// order — the front one is answered first; deciding it exposes the next.
    /// </summary>
    public ApprovalGateView BeginApprovalGate(string toolName, string detail, string? invocationId = null, int generation = 1, bool isStreaming = false)
    {
        var gate = new ApprovalGateView(toolName, SanitizeGateDetail(detail, isStreaming), invocationId, generation);
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
        if (gate.Decision == ApprovalChoice.AlwaysAllow)
        {
            RecordAlwaysPattern(gate.ToolName);
            MachineFor(gate.Id).ConfirmAlways(AlwaysPatternFor(gate.ToolName));
        }
        else if (gate.Decision == ApprovalChoice.Deny)
        {
            MachineFor(gate.Id).ConfirmReject(GetRejectReason(gate.Id));
        }

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

/// <summary>
/// Position in the opencode-style 3-stage permission flow
/// ([steal/opencode] #1170, epic #1155): the user answers the permission
/// prompt (<see cref="Permission"/>), optionally steps into the
/// always-stage to pick a scoped pattern (<see cref="Always"/>) or into
/// the reject-stage to type feedback for the agent (<see cref="Reject"/>),
/// then confirms or cancels back to permission.
/// </summary>
public enum PermissionStage : byte
{
    /// <summary>Initial prompt: approve once / always / deny.</summary>
    Permission = 0,

    /// <summary>Pattern list (<c>permissionAlwaysLines</c>) — confirm persists a scoped always-rule.</summary>
    Always,

    /// <summary>Message field — confirm denies and sends the text back to the agent as feedback.</summary>
    Reject,
}

/// <summary>
/// Pure per-gate 3-stage machine behind <see cref="ApprovalGateRouter"/>
/// (opencode <c>mini/permission.shared.ts</c>): once/always/reject intent
/// becomes confirm/cancel. No UI, no threads, no coordinator — the router
/// owns persistence (always-patterns) and stamping; this type only tracks
/// the stage plus the confirmed payload. Terminal confirms keep their
/// stage (the gate is decided); <see cref="Cancel"/> returns to
/// <see cref="PermissionStage.Permission"/>.
/// </summary>
public sealed class PermissionMachine
{
    /// <summary>Current stage (starts at <see cref="PermissionStage.Permission"/>).</summary>
    public PermissionStage Stage { get; private set; } = PermissionStage.Permission;

    /// <summary>Pattern confirmed at the always-stage (empty until <see cref="ConfirmAlways"/>).</summary>
    public string ConfirmedPattern { get; private set; } = string.Empty;

    /// <summary>Message confirmed at the reject-stage (empty until <see cref="ConfirmReject"/>).</summary>
    public string ConfirmedRejectMessage { get; private set; } = string.Empty;

    /// <summary>Enters the always-stage; false when already past permission (cancel first).</summary>
    public bool RequestAlways()
    {
        if (Stage != PermissionStage.Permission)
        {
            return false;
        }

        Stage = PermissionStage.Always;
        return true;
    }

    /// <summary>Enters the reject-stage; false when already there.</summary>
    public bool RequestReject()
    {
        if (Stage == PermissionStage.Reject)
        {
            return false;
        }

        Stage = PermissionStage.Reject;
        return true;
    }

    /// <summary>Abandons the always-/reject-stage, back to permission (payloads are kept for audit).</summary>
    public void Cancel() => Stage = PermissionStage.Permission;

    /// <summary>Confirms the always-stage with a scoped <paramref name="pattern"/> (blank coerces to <c>"?:*"</c>).</summary>
    public void ConfirmAlways(string? pattern)
    {
        ConfirmedPattern = string.IsNullOrWhiteSpace(pattern) ? "?:*" : pattern.Trim();
        Stage = PermissionStage.Always;
    }

    /// <summary>Confirms the reject-stage with <paramref name="message"/> feedback for the agent.</summary>
    public void ConfirmReject(string? message)
    {
        ConfirmedRejectMessage = (message ?? string.Empty).Trim();
        Stage = PermissionStage.Reject;
    }
}
