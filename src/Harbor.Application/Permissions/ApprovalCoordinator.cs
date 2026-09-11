using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Permissions;

/// <summary>
///     Runtime-owned approval/cancellation coordinator (#49 PR1).
///     Single lock orders decisions vs cancellation per gate; every blocking
///     or reentrant action (CTS cancel, TCS completion) happens outside it.
/// </summary>
public sealed class ApprovalCoordinator(ILogger<ApprovalCoordinator> logger) : IApprovalCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GateSlot> _gates = new(StringComparer.Ordinal);

    private sealed class GateSlot
    {
        public TaskCompletionSource<ApprovalResolution?> Tcs { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Decided;
        public ApprovalResolution? Decision;
        public bool Cancelled;
    }

    /// <inheritdoc />
    public void RegisterGate(string gateId)
    {
        ArgumentException.ThrowIfNullOrEmpty(gateId);
        lock (_gate)
        {
            _gates.TryAdd(gateId, new GateSlot());
        }
    }

    /// <inheritdoc />
    public ApprovalDecisionDisposition DecideApproval(string gateId, ApprovalResolution decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        GateSlot? slot;
        ApprovalDecisionDisposition disposition;
        lock (_gate)
        {
            if (!_gates.TryGetValue(gateId, out slot))
            {
                return ApprovalDecisionDisposition.StaleGate;
            }

            if (slot.Cancelled)
            {
                disposition = ApprovalDecisionDisposition.AlreadyCancelled;
            }
            else if (slot.Decided)
            {
                disposition = ApprovalDecisionDisposition.AlreadyDecided;
            }
            else
            {
                slot.Decided = true;
                slot.Decision = decision;
                disposition = ApprovalDecisionDisposition.Accepted;
            }
        }

        // Complete outside the lock: continuations run on the completer.
        if (disposition == ApprovalDecisionDisposition.Accepted)
        {
            slot!.Tcs.TrySetResult(decision);
        }

        return disposition;
    }

    /// <inheritdoc />
    public async Task<ApprovalResolution?> WaitForDecisionAsync(string gateId, CancellationToken ct)
    {
        GateSlot slot;
        lock (_gate)
        {
            if (!_gates.TryGetValue(gateId, out slot!))
            {
                throw new InvalidOperationException($"Unknown approval gate '{gateId}' — RegisterGate first.");
            }
        }

        if (ct.CanBeCanceled)
        {
            // Cancelled token → fail closed. TrySetResult is idempotent: a raced
            // decision keeps its win, a raced RequestCancel keeps its sweep.
            using var reg = ct.Register(static state => ((GateSlot)state!).Tcs.TrySetResult(null), slot);
            var result = await slot.Tcs.Task.ConfigureAwait(false);
            ForgetGate(gateId, slot);
            return result;
        }

        var outcome = await slot.Tcs.Task.ConfigureAwait(false);
        ForgetGate(gateId, slot);
        return outcome;
    }

    /// <inheritdoc />
    public void RequestCancel(IAgentRunner agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        List<TaskCompletionSource<ApprovalResolution?>> swept;
        lock (_gate)
        {
            // Sweep only undecided gates: a decision that won the race before
            // this cancel keeps its win (the run token below still aborts the
            // execution — cancel always kills the run, it just doesn't rewrite
            // an already-recorded decision). Swept entries are dropped as their
            // waiters consume them (ForgetGate); every Register pairs with a
            // Wait in the asker flow, so the registry stays bounded by live gates.
            swept = new List<TaskCompletionSource<ApprovalResolution?>>(_gates.Count);
            foreach (var slot in _gates.Values)
            {
                if (!slot.Decided && !slot.Cancelled)
                {
                    slot.Cancelled = true;
                    swept.Add(slot.Tcs);
                }
            }
        }

        // Outside the lock: CTS.Cancel runs continuations synchronously.
        try
        {
            agent.AbortSource.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogDebug(ex, "AbortSource already disposed during coordinated cancel");
        }

        foreach (var tcs in swept)
        {
            tcs.TrySetResult(null);
        }

        logger.LogDebug("Coordinated cancel swept {GateCount} pending gate(s)", swept.Count);
    }

    private void ForgetGate(string gateId, GateSlot slot)
    {
        lock (_gate)
        {
            // Remove only our own slot: a re-registered gate (same id, new slot)
            // after consume must survive.
            if (_gates.TryGetValue(gateId, out var current) && ReferenceEquals(current, slot))
            {
                _gates.Remove(gateId);
            }
        }
    }
}
