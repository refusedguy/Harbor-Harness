using System.Collections.Concurrent;

namespace Harbor.Application.Agents;

/// <summary>
///     Outcome of presenting a command id to a receipt store (issue #2662,
///     idempotent command receipts). A command id is the idempotency key: the
///     first presentation records a receipt, every later one is a duplicate
///     and must not drive the command's side effects again.
/// </summary>
public enum CommandReceiptOutcome
{
    /// <summary>No receipt existed for the command id; this call recorded it.</summary>
    Recorded,

    /// <summary>A receipt already existed; the call is a replay.</summary>
    Duplicate,
}

/// <summary>
///     The durable fact that a command id was accepted for one run and graph
///     node (issue #2662). The recorded timestamp is supplied by the caller so
///     a replay is fully deterministic and does not mint a second receipt.
/// </summary>
/// <param name="CommandId">Idempotency key of the command.</param>
/// <param name="RunId">Run the command belongs to.</param>
/// <param name="NodeId">Execution-graph node the command belongs to.</param>
/// <param name="RecordedAtUtc">When the receipt was first recorded.</param>
public sealed record CommandReceipt(
    string CommandId,
    string RunId,
    string NodeId,
    DateTimeOffset RecordedAtUtc);

/// <summary>
///     The receipt that won the idempotency key together with the outcome of
///     this call. On a duplicate the receipt is the FIRST one recorded, so a
///     replay observes the original run, node and timestamp.
/// </summary>
/// <param name="Receipt">The receipt that owns the command id.</param>
/// <param name="Outcome">Whether this call recorded it or found it already present.</param>
public sealed record CommandReceiptSubmission(
    CommandReceipt Receipt,
    CommandReceiptOutcome Outcome)
{
    /// <summary>Whether this call recorded a new receipt.</summary>
    public bool IsRecorded => Outcome == CommandReceiptOutcome.Recorded;

    /// <summary>Whether this call was a replay of an already recorded command.</summary>
    public bool IsDuplicate => Outcome == CommandReceiptOutcome.Duplicate;
}

/// <summary>
///     Stores command receipts by idempotency key (issue #2662). A storage
///     implementation promises one receipt per command id: recording the same
///     id twice returns the first receipt with a <see cref="CommandReceiptOutcome.Duplicate" />
///     outcome, so a caller that acts only on <see cref="CommandReceiptSubmission.IsRecorded" />
///     cannot repeat a command's side effects.
/// </summary>
public interface ICommandReceiptStore
{
    /// <summary>
    ///     Record a receipt for a command id, or return the receipt already
    ///     recorded for it. The operation is atomic for one command id.
    /// </summary>
    /// <param name="commandId">Idempotency key of the command.</param>
    /// <param name="runId">Run the command belongs to.</param>
    /// <param name="nodeId">Execution-graph node the command belongs to.</param>
    /// <param name="recordedAtUtc">Timestamp to store when the receipt is new.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The winning receipt and this call's outcome.</returns>
    Task<CommandReceiptSubmission> TryRecordAsync(
        string commandId,
        string runId,
        string nodeId,
        DateTimeOffset recordedAtUtc,
        CancellationToken ct = default);

    /// <summary>Read the receipt for a command id, if one was recorded.</summary>
    /// <param name="commandId">Idempotency key to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The first receipt recorded for the id, or null.</returns>
    Task<CommandReceipt?> GetAsync(string commandId, CancellationToken ct = default);
}

/// <summary>
///     In-process receipt store used by the agent runtime (issue #2662). It
///     is the first, storage-agnostic slice of the command-receipt contract:
///     an ordinal-keyed atomic map, with no reflection, no dynamic code and no
///     platform-specific API, so it stays NativeAOT-safe. A SQLite-backed
///     implementation can replace it without changing the contract or the
///     idempotency rule it enforces.
/// </summary>
public sealed class InMemoryCommandReceiptStore : ICommandReceiptStore
{
    private readonly ConcurrentDictionary<string, CommandReceipt> _receipts =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<CommandReceiptSubmission> TryRecordAsync(
        string commandId,
        string runId,
        string nodeId,
        DateTimeOffset recordedAtUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var receipt = new CommandReceipt(commandId, runId, nodeId, recordedAtUtc);
        if (_receipts.TryAdd(commandId, receipt))
            return Task.FromResult(new CommandReceiptSubmission(receipt, CommandReceiptOutcome.Recorded));

        // The key is present because TryAdd lost the race; the stored value is
        // the first receipt, never the one this caller tried to add.
        return Task.FromResult(
            new CommandReceiptSubmission(_receipts[commandId], CommandReceiptOutcome.Duplicate));
    }

    /// <inheritdoc />
    public Task<CommandReceipt?> GetAsync(string commandId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        _receipts.TryGetValue(commandId, out var receipt);
        return Task.FromResult<CommandReceipt?>(receipt);
    }
}
