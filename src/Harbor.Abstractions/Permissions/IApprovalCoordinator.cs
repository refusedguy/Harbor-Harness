using Harbor.Abstractions.Agents;

namespace Harbor.Abstractions.Permissions;

/// <summary>
///     Disposition of an <see cref="IApprovalCoordinator.DecideApproval" /> call.
/// </summary>
public enum ApprovalDecisionDisposition
{
    /// <summary>The decision won the race and was recorded for the waiter.</summary>
    Accepted,

    /// <summary>A decision for this gate was already recorded; the late one touched nothing.</summary>
    AlreadyDecided,

    /// <summary>Cancellation won first; the gate is closed, the late decision touched nothing.</summary>
    AlreadyCancelled,

    /// <summary>Unknown gate id (stale view, double-consume, or never registered).</summary>
    StaleGate,
}

/// <summary>
///     UI-agnostic approval resolution. The coordinator never sees widgets or
///     key events — routers map their <c>Approve/Deny/Always</c> choices into
///     this shape, askers map it back into <c>PermissionResponse</c>.
/// </summary>
/// <param name="Approved">Whether the tool call may proceed.</param>
/// <param name="PersistDecision">Whether the decision is recorded as an "always" rule.</param>
public sealed record ApprovalResolution(bool Approved, bool PersistDecision);

/// <summary>
///     Runtime-owned approval/cancellation coordinator (#49 PR1): the single
///     linearization point for approval decisions vs run cancellation.
/// </summary>
/// <remarks>
///     <para>
///         Today approval waits (asker TCS on the gate view) and cancellation
///         (<c>AbortSource.Cancel()</c> from 6 call sites) meet nowhere: a
///         cancel racing a keypress can approve-then-kill or hang a waiter.
///         All runtime cancel ingresses funnel through
///         <see cref="RequestCancel" />; all gate decisions funnel through
///         <see cref="DecideApproval" />. One short lock orders them — first
///         event wins per gate — and no user code, callbacks, or
///         <c>CTS.Cancel()</c> ever runs under that lock (Cancel runs
///         continuations synchronously; reentrancy would deadlock).
///     </para>
///     <para>
///         PR1 covers ingress + decision stamping. The execution-commit state
///         machine (<c>Ready → Executing</c>, <c>GateId/InvocationId/Generation</c>
///         identity) is PR2 and builds on these dispositions.
///     </para>
/// </remarks>
public interface IApprovalCoordinator
{
    /// <summary>
    ///     Open a waitable gate. Idempotent — re-registering a live gate is a no-op.
    ///     A gate registered after a cancel still waits normally (cancel is edge-triggered,
    ///     mirroring <c>AbortSource</c> semantics; it is not a latched deny).
    /// </summary>
    void RegisterGate(string gateId);

    /// <summary>
    ///     Record a decision for a gate. Exactly one decision wins per gate;
    ///     late or unknown ids never mutate state (see <see cref="ApprovalDecisionDisposition" />).
    /// </summary>
    ApprovalDecisionDisposition DecideApproval(string gateId, ApprovalResolution decision);

    /// <summary>
    ///     Wait for a gate's decision. Returns <see langword="null" /> when cancellation
    ///     wins (the linked token fired or <see cref="RequestCancel" /> swept the gate) —
    ///     callers fail closed (deny). Unknown gate ids throw (programmer error: register first).
    /// </summary>
    Task<ApprovalResolution?> WaitForDecisionAsync(string gateId, CancellationToken ct);

    /// <summary>
    ///     The single runtime cancellation ingress: replaces every direct
    ///     <c>AbortSource.Cancel()</c> call. Marks pending gates cancelled (their
    ///     waiters complete with <see langword="null" />), then cancels the
    ///     run source outside the lock. Safe to call when idle or twice.
    /// </summary>
    void RequestCancel(IAgentRunner agent);
}
