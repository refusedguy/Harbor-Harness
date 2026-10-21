// JsonlGoalStore.cs — the goal axis of the JSONL backend (#1761): the durable
// goal a session is working toward, recorded with the session and read back
// from it.
//
// This is the caretaker half of the axis, the same shape the feedback axis
// has (#1746): it resolves the session file, decides the session existence
// policy, keeps the on-disk line shapes and scans them back — while the store
// keeps the forwarders, so the ISessionGoalStore implementation site stays in
// JsonlSessionStore.
//
// THE FILE FORMAT IS THE POINT
// ----------------------------
// The goal lives in its OWN lines ("type":"goal"), never as a field of a
// message. The message read path skips them the way it skips the checkpoint
// markers, so:
//
//   * the messages the model context is built from carry no goal record — the
//     rule is a property of the format, not a promise;
//   * a session file written before the goal axis existed reads exactly as it
//     did: there is no goal line in it, and its absence is not a malformed
//     line.
//
// WHAT IS APPENDED, AND WHAT A CONFLICT IS
// ----------------------------------------
// The axis is append-only, like the rest of the store: every change appends a
// WHOLE snapshot of the goal, and the effective goal is the newest snapshot.
// The phases a goal went through therefore stay in the log.
//
// The snapshot carries a revision, and that is where the conflict is decided:
// an append whose revision is not exactly one past the stored one is REFUSED
// before anything is written. Two writers of a goal are the round driver
// (which counts rounds) and the agent (which completes or blocks it), and they
// can be writing at the same moment. The loser of that race re-reads and
// decides again; it does not overwrite the newer fact with its older one.
//
// THE SHAPE OF THE RAIL
// ---------------------
// The session file is resolved ONCE per call, on one rail: the resolution and
// the existence policy are a Bind link, and the work hangs off it. The
// expected outcomes (no such session, a stale revision) are decided BEFORE the
// try boundary, so they stay plain failures — no throw, no error log (#199).
// Only the IO rides the try.

using Harbor.Storage.Shared;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The goal axis of the JSONL backend (#1761): the durable goal a session
///     is working toward.
/// </summary>
internal sealed class JsonlGoalStore : ISessionGoalStore
{
    /// <summary>
    ///     The store this axis belongs to: its per-session lock strip and its
    ///     parsed-message cache. The axis borrows them, never copies.
    /// </summary>
    private readonly JsonlSessionStore _owner;

    /// <summary>
    ///     Root directory of the session files — the store's own, so a goal
    ///     write resolves the very same file a message does.
    /// </summary>
    private readonly string _rootDirectory;

    /// <summary>
    ///     Logger of the owning store — the log category stays the store's.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     Bind the axis to its store.
    /// </summary>
    /// <param name="owner">The store that owns the session files.</param>
    /// <param name="rootDirectory">Root directory of the session files.</param>
    /// <param name="logger">Logger of the owning store.</param>
    internal JsonlGoalStore(JsonlSessionStore owner, string rootDirectory, ILogger logger)
    {
        _owner = owner;
        _rootDirectory = rootDirectory;
        _logger = logger;
    }

    /// <summary>
    ///     Read the session's effective goal: the newest snapshot of it.
    /// </summary>
    /// <remarks>
    ///     A session with no goal line yields <see cref="Maybe{T}.None" /> —
    ///     not a failure. "No goal" is a state a session can be in, and the
    ///     difference between "no goal" and "the store could not answer" is
    ///     the difference between a caller that starts no rounds and a caller
    ///     that reports an error.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The goal, or none when the session has none.</returns>
    public Task<Result<Maybe<Goal>>> GetGoalAsync(
        string sessionId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        return ResolveSessionFile(sessionId)
            .Bind(sessionFile => ReadGoalAsync(sessionFile, sessionId, ct));
    }

    /// <summary>
    ///     Append a new revision of the session's goal.
    /// </summary>
    /// <remarks>
    ///     The revision check and the append happen under the SAME per-session
    ///     lock, so two concurrent writers cannot both believe they are
    ///     writing revision N+1: the second one finds the first one's line and
    ///     is refused.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="goal">The goal revision to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended goal, or failure when the revision is stale.</returns>
    public Task<Result<Goal>> AppendGoalAsync(
        string sessionId, Goal goal, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();

        return ResolveSessionFile(sessionId)
            .Bind(sessionFile => AppendGoalAsync(sessionFile, sessionId, goal, ct));
    }

    /// <summary>
    ///     Append the goal line under the per-session lock, after the checks
    ///     that decide whether the write may happen at all.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="goal">The goal revision to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended goal, or failure when the revision is stale.</returns>
    private async Task<Result<Goal>> AppendGoalAsync(
        string sessionFile, string sessionId, Goal goal, CancellationToken ct)
    {
        // #199: the expected outcome is decided BEFORE the try boundary, so it
        // stays a plain failure (no throw, no error log).
        if (string.IsNullOrWhiteSpace(goal.Objective))
        {
            return Result.Failure<Goal>(
                $"A goal in session '{sessionId}' must state an objective.");
        }

        if (goal.MaxRounds <= 0)
        {
            return Result.Failure<Goal>(
                $"A goal in session '{sessionId}' must allow at least one round.");
        }

        return await Result.Try(async () =>
        {
            var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                // The current goal is read INSIDE the lock, so the revision
                // check and the append see one state of the file and not two.
                Goal? current = ReadGoal(sessionFile, sessionId, _logger);
                int expected = current is { } stored ? stored.Revision + 1 : 1;
                if (goal.Revision != expected)
                {
                    return Result.Failure<Goal>(
                        $"The goal of session '{sessionId}' is at revision {current?.Revision ?? 0}; "
                        + $"revision {goal.Revision} was refused.");
                }

                byte[] line = SessionFileIO.EncodeLine(
                    // #1740: the objective and the block reason are free text
                    // the user (or the agent) typed, and a credential can be in
                    // them as easily as in a tool result.
                    SessionGoalMapperKit.ToRow(goal with
                    {
                        Objective = _owner.ScrubTextForWrite(goal.Objective),
                        BlockedReason = _owner.ScrubOptionalTextForWrite(goal.BlockedReason),
                    }),
                    JsonlCodecContext.Default.GoalEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
                _owner.InvalidateMessageCache(sessionId);
                return Result.Success(goal);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(x => x)
            .TapError(e => JsonlSessionStoreLog.AppendGoalFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Read the session's goal under the per-session lock.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The goal, or none when the session has none.</returns>
    private async Task<Result<Maybe<Goal>>> ReadGoalAsync(
        string sessionFile, string sessionId, CancellationToken ct)
    {
        return await Result.Try(async () =>
        {
            var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                Goal? goal = ReadGoal(sessionFile, sessionId, _logger);
                return Result.Success(goal is { } found ? Maybe<Goal>.From(found) : Maybe<Goal>.None);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .Bind(x => x)
            .TapError(e => JsonlSessionStoreLog.ReadGoalFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Resolve the session's file and decide the existence policy: a
    ///     session id that cannot name a file, and a session whose file is
    ///     not there, are both plain failures decided before anything is
    ///     written.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The session file path, or failure when the session is not there.</returns>
    private Result<string> ResolveSessionFile(string sessionId) =>
        SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId)
            .Bind(path =>
            {
                if (!File.Exists(path))
                {
                    _owner.InvalidateMessageCache(sessionId);
                    return Result.Failure<string>(SessionStoreErrors.SessionNotFound(sessionId));
                }

                return Result.Success(path);
            });

    /// <summary>
    ///     Sync byte-level scan of one session file for the goal lines. The
    ///     newest snapshot wins; a line this build cannot read is skipped with
    ///     a warning (§ROP-001), so one bad line never costs the session its
    ///     goal.
    /// </summary>
    /// <param name="sessionFile">The session file.</param>
    /// <param name="sessionId">The session id (for the warnings).</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    /// <returns>The effective goal, or null when the session has none.</returns>
    private static Goal? ReadGoal(string sessionFile, string sessionId, ILogger logger)
    {
        Goal? effective = null;

        using var source = new FileStream(
            sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        using var reader = new ChunkedLineReader(source);
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                effective = FoldGoalRecord(record, sessionId, effective, logger) ?? effective;
            }
        }

        if (reader.TryGetTrailingRecord(out var trailing))
        {
            effective = FoldGoalRecord(trailing, sessionId, effective, logger) ?? effective;
        }

        return effective;
    }

    /// <summary>
    ///     Fold one record into the goal. A record that is not a goal line is
    ///     not this method's business; a goal line that cannot be read warns
    ///     and is skipped (§ROP-001).
    /// </summary>
    /// <param name="record">The record to fold.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="effective">The goal in force so far.</param>
    /// <param name="logger">Sink for the malformed-line warnings.</param>
    /// <returns>The goal in force after the record, or null when the record changed nothing.</returns>
    private static Goal? FoldGoalRecord(
        ReadOnlySpan<byte> record,
        string sessionId,
        Goal? effective,
        ILogger logger)
    {
        if (!SessionFileReader.IsGoalLine(record))
        {
            return null;
        }

        GoalEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.GoalEntry);
        }
        catch (JsonException ex)
        {
            JsonlSessionStoreLog.SkippingMalformedGoalLine(logger, ex, sessionId);
            return null;
        }

        if (entry is not { Type: "goal", Objective: { Length: > 0 } })
        {
            JsonlSessionStoreLog.GoalLineMissingObjective(logger, sessionId);
            return null;
        }

        // A value this build does not know is a record from a newer build. It
        // is skipped, not guessed at: reading it as "active" would put a
        // phase in the goal the writer never set.
        if (!SessionGoalMapperKit.TryParsePhase(entry.Phase, out GoalPhase phase))
        {
            JsonlSessionStoreLog.SkippingUnknownGoalPhase(logger, sessionId, entry.Phase);
            return null;
        }

        GoalBlockCode? code = null;
        if (!string.IsNullOrEmpty(entry.BlockedCode))
        {
            if (!SessionGoalMapperKit.TryParseBlockCode(entry.BlockedCode, out GoalBlockCode parsed))
            {
                JsonlSessionStoreLog.SkippingUnknownGoalPhase(logger, sessionId, entry.BlockedCode);
                return null;
            }

            code = parsed;
        }

        return SessionGoalMapperKit.ToContract(entry, phase, code);
    }
}
