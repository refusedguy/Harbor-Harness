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

    /// <summary>Unknown gate id, or invocation/generation mismatch against the gate's binding (stale view, double-consume, never registered, or wrong-identity replay — touches nothing).</summary>
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
///         (<c>RequestAbort()</c> from 6 call sites) meet nowhere: a
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
///         identity) is PR2 and builds on these dispositions. PR4 binds the same
///         identity at the gate: the UI sends the full 4-tuple
///         <c>(GateId, InvocationId, Generation, Choice)</c> and the coordinator
///         validates every component — a mismatch touches nothing (fail closed).
///     </para>
/// </remarks>
public interface IApprovalCoordinator
{
    /// <summary>
    ///     Open a waitable gate. Idempotent — re-registering a live gate is a no-op.
    ///     A gate registered after a cancel still waits normally (cancel is edge-triggered,
    ///     mirroring <c>AbortToken</c> semantics; it is not a latched deny).
    /// </summary>
    void RegisterGate(string gateId);

    /// <summary>
    ///     Open a waitable gate bound to one execution attempt (#49 PR4).
    ///     Idempotent — re-registering a live gate keeps the first binding.
    ///     The 4-tuple <see cref="DecideApproval(string, string, int, ApprovalResolution)" />
    ///     accepts a decision only when all three identity components match.
    /// </summary>
    /// <param name="gateId">Gate id (same as <see cref="RegisterGate(string)" />).</param>
    /// <param name="invocationId">Tool-call id, unique per requested execution.</param>
    /// <param name="generation">1-based attempt (retries bump it, mirroring <see cref="TryCommitApproval" />).</param>
    void RegisterGate(string gateId, string invocationId, int generation);

    /// <summary>
    ///     Record a decision for a gate. Exactly one decision wins per gate;
    ///     late or unknown ids never mutate state (see <see cref="ApprovalDecisionDisposition" />).
    /// </summary>
    ApprovalDecisionDisposition DecideApproval(string gateId, ApprovalResolution decision);

    /// <summary>
    ///     Record a UI decision carrying the full 4-tuple identity (#49 PR4):
    ///     the gate, the invocation, and the generation must ALL match the
    ///     binding recorded by <see cref="RegisterGate(string, string, int)" />.
    ///     Any mismatch returns <see cref="ApprovalDecisionDisposition.StaleGate" />,
    ///     touches nothing, and is logged — the waiter stays pending until the
    ///     genuine decision or cancellation arrives (fail closed: a wrong-identity
    ///     approve can never approve the wrong gate). Race outcomes keep their
    ///     PR1 dispositions (<c>AlreadyCancelled</c> / <c>AlreadyDecided</c>).
    /// </summary>
    ApprovalDecisionDisposition DecideApproval(
        string gateId, string invocationId, int generation, ApprovalResolution decision);

    /// <summary>
    ///     Wait for a gate's decision. Returns <see langword="null" /> when cancellation
    ///     wins (the linked token fired or <see cref="RequestCancel" /> swept the gate) —
    ///     callers fail closed (deny). Unknown gate ids throw (programmer error: register first).
    /// </summary>
    Task<ApprovalResolution?> WaitForDecisionAsync(string gateId, CancellationToken ct);

    /// <summary>
    ///     The single runtime cancellation ingress: replaces every direct
    ///     <see cref="IAgentRunner.RequestAbort()" /> call. Marks pending gates cancelled (their
    ///     waiters complete with <see langword="null" />), then cancels the
    ///     run source outside the lock. Safe to call when idle or twice.
    /// </summary>
    void RequestCancel(IAgentRunner agent);

    /// <summary>
    ///     Open an execution-commit scope (#49 PR2). Captures the current
    ///     cancel generation: a <see cref="RequestCancel" /> issued after this
    ///     call invalidates the scope. Scopes are epoch-scoped, not once-only —
    ///     parallel tool calls approved in the same epoch share fate by design.
    /// </summary>
    long BeginApprovalScope();

    /// <summary>
    ///     Commit barrier between approval (Ready) and execution (Executing).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Returns <see langword="true" /> iff no <see cref="RequestCancel" />
    ///         happened after the scope was issued AND this
    ///         <c>(invocationId, generation)</c> pair was not committed before —
    ///         i.e. cancellation did NOT win before commit and this is not a
    ///         duplicate/stale attempt, so the tool may start. Otherwise returns
    ///         <see langword="false" /> and the caller must NOT start the tool
    ///         (fail closed).
    ///     </para>
    ///     <para>
    ///         Identity: <c>invocationId</c> is the tool-call id, unique per
    ///         requested execution; <c>generation</c> is the 1-based attempt
    ///         (retries bump it — a retried attempt commits a NEW generation,
    ///         a replayed old one is rejected as stale). Higher generation for
    ///         a live invocation supersedes; equal-or-lower is rejected, which
    ///         bounds duplicate dispatch to a single start.
    ///         <see cref="CompleteInvocation" /> retires the record afterwards.
    ///     </para>
    /// </remarks>
    bool TryCommitApproval(long scope, string invocationId, int generation);

    /// <summary>
    ///     Retire an invocation record after its terminal outcome (success,
    ///     error, or cancellation) into a bounded tombstone set. Unknown ids
    ///     are ignored. After retirement any late commit for the id fails
    ///     (stale touches nothing); oldest tombstones are evicted past a cap.
    /// </summary>
    void CompleteInvocation(string invocationId);
}
