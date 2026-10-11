// SessionCheckpointRunner.cs — CLI face of `harbor sessions checkpoint|checkpoints|rewind`
// (#1247): validation guards plus argument mapping over the checkpoint seam
// (ISessionCheckpointStore) — load-and-cut semantics, Result in / Result out.
// Mirrors SessionForkRunner; the store stays the only place that touches the
// file (or the database).

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CLI runner for session checkpoints. Both durable backends implement
///     <see cref="ISessionCheckpointStore" /> (jsonl since slice 1, sqlite
///     since slice 2) — any other <see cref="ISessionStore" /> fails fast
///     with that message instead of a cast exception.
/// </summary>
public sealed class SessionCheckpointRunner
{
    private const string BackendRequired =
        "Checkpoints need the jsonl or sqlite backend (HARBOR_STORAGE=jsonl|sqlite).";

    private readonly ISessionStore _store;

    public SessionCheckpointRunner(ISessionStore store) =>
        _store = store;

    /// <summary>Mark <paramref name="messageId" /> in <paramref name="sessionId" />.</summary>
    public async Task<Result<SessionCheckpoint>> CheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<SessionCheckpoint>("Session id must not be empty.");
        if (string.IsNullOrWhiteSpace(messageId))
            return Result.Failure<SessionCheckpoint>("Message id must not be empty.");
        if (_store is not ISessionCheckpointStore checkpoints)
            return Result.Failure<SessionCheckpoint>(BackendRequired);

        return await checkpoints.CreateCheckpointAsync(sessionId, messageId, label, ct).ConfigureAwait(false);
    }

    /// <summary>List checkpoints (and rewind trail markers) of <paramref name="sessionId" />.</summary>
    public async Task<Result<IReadOnlyList<SessionCheckpoint>>> ListAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<IReadOnlyList<SessionCheckpoint>>("Session id must not be empty.");
        if (_store is not ISessionCheckpointStore checkpoints)
            return Result.Failure<IReadOnlyList<SessionCheckpoint>>(BackendRequired);

        return await checkpoints.ListCheckpointsAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Rewind <paramref name="sessionId" /> to <paramref name="checkpointId" />.</summary>
    public async Task<Result<SessionRewindOutcome>> RewindAsync(
        string sessionId, string checkpointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<SessionRewindOutcome>("Session id must not be empty.");
        if (string.IsNullOrWhiteSpace(checkpointId))
            return Result.Failure<SessionRewindOutcome>("Checkpoint id must not be empty.");
        if (_store is not ISessionCheckpointStore checkpoints)
            return Result.Failure<SessionRewindOutcome>(BackendRequired);

        return await checkpoints.RewindToCheckpointAsync(sessionId, checkpointId, ct).ConfigureAwait(false);
    }
}
