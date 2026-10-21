// JsonlCheckpointStore.cs — #1513 (#95 slice): the checkpoint/rewind axis of
// the JSONL store, moved verbatim out of JsonlSessionStore.cs.
//
// The store keeps thin forwarders, so the ISessionCheckpointStore impl site
// stays where it is and every existing call site keeps working; the
// checkpoint surface itself has one home. Caller migration + forwarder
// deletion ride in #1392 (one extract per PR, spec §9).
//
// Verbatim move: the bodies below are the store's own, unchanged apart from
// three owner-threaded seams, each marked in place:
//
//   * the per-session lock  -> _owner.GetSessionLockAsync(...) — the STORE's
//     lock strip, not a copy: a checkpoint append still serializes against a
//     message append on the same session.
//   * the parse cache       -> _owner.InvalidateMessageCache(...) — the
//     STORE's cache, so a checkpoint write drops the same entry a message
//     write does.
//   * the originator        -> _owner.CaptureCheckpointAsync /
//     _owner.RestoreCheckpointAsync — #1551 (Memento): the store still owns
//     the state a capture/restore concerns, so the originator stays the
//     store (SessionCheckpointOriginator.cs); this file is its caretaker
//     half.
//
// Behaviour is unchanged: same trail markers, same MessageNotFound /
// CheckpointNotFound failures, same honest-trail line — see
// tests/Harbor.Storage.Jsonl.Tests/JsonlCheckpointsTests.cs.

using Harbor.Storage.Shared;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The checkpoint/rewind axis of the JSONL backend (#1247), extracted
///     verbatim from <see cref="JsonlSessionStore" /> (#1513, #95 slice: one
///     extract per PR).
/// </summary>
/// <remarks>
///     <para>
///         This is the caretaker half of the checkpoint pattern (#1551
///         Memento): it resolves the session file, decides the session
///         existence policy, keeps and numbers the on-disk
///         <see cref="CheckpointEntry" /> rows and scans them back — while
///         the store keeps the originator, which owns the state a
///         capture/restore concerns.
///     </para>
///     <para>
///         The store keeps thin forwarders over this type, so the
///         <see cref="ISessionCheckpointStore" /> implementation site is
///         unchanged; the row shapes and the contract row conversions still
///         go through the single mapper zone
///         (<see cref="SessionCheckpointMapperKit" />).
///     </para>
/// </remarks>
internal sealed class JsonlCheckpointStore : ISessionCheckpointStore
{
    /// <summary>
    ///     The store this axis belongs to: its per-session lock strip, its
    ///     parsed-message cache and its originator (#1551 Memento).
    /// </summary>
    private readonly JsonlSessionStore _owner;

    /// <summary>
    ///     Root directory of the session files — the store's own, so a
    ///     checkpoint resolves the very same file a message does.
    /// </summary>
    private readonly string _rootDirectory;

    /// <summary>
    ///     Logger of the owning store — the log category stays the store's.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     Bind the axis to its store. The store's own state (lock strip,
    ///     parse cache, originator) is borrowed, never copied.
    /// </summary>
    /// <param name="owner">The store that owns the session files.</param>
    /// <param name="rootDirectory">Root directory of the session files.</param>
    /// <param name="logger">Logger of the owning store.</param>
    internal JsonlCheckpointStore(JsonlSessionStore owner, string rootDirectory, ILogger logger)
    {
        _owner = owner;
        _rootDirectory = rootDirectory;
        _logger = logger;
    }

    /// <summary>
    ///     Mark a checkpoint over a message (#1247): the anchor's zero-based
    ///     index in message order is recorded as a <c>"type":"checkpoint"</c>
    ///     line appended to the session file, so a later
    ///     <see cref="RewindToCheckpointAsync" /> can restore it by
    ///     checkpoint id instead of message id. Checkpoints are metadata —
    ///     the message read path skips them, so history, stats and export
    ///     are unchanged by their presence.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#1551 (Memento):</b> this member is the caretaker half of
    ///         the pattern — it resolves the file, decides the session
    ///         existence policy, then hands the capture over to the
    ///         originator (<see cref="JsonlSessionStore.CaptureCheckpointAsync" /> in
    ///         <c>SessionCheckpointOriginator.cs</c>). The anchor scan, the
    ///         checkpoint's id/index construction and the persisted line
    ///         shape belong to the originator and the mapper kit; the store
    ///         keeps and numbers snapshots but never opens one.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Anchor message id to mark.</param>
    /// <param name="label">Optional human label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded checkpoint, or failure if session/message not found.</returns>
    public async Task<Result<SessionCheckpoint>> CreateCheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return resolved.ConvertFailure<SessionCheckpoint>();
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        if (!File.Exists(sessionFile))
        {
            _owner.InvalidateMessageCache(sessionId);
            return Result.Failure<SessionCheckpoint>(SessionStoreErrors.SessionNotFound(sessionId));
        }

        // #1551: capture is originator work — the store neither scans the
        // anchor (no index arithmetic here) nor builds the checkpoint row.
        return await _owner.CaptureCheckpointAsync(sessionId, sessionFile, messageId, label, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     List checkpoints (and rewind trail markers) for a session in file
    ///     order (#1247). Unparseable checkpoint lines are
    ///     skipped with a warning, never a whole-list failure (§ROP-001).
    /// </summary>
    public Task<Result<IReadOnlyList<SessionCheckpoint>>> ListCheckpointsAsync(
        string sessionId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return Task.FromResult(resolved.ConvertFailure<IReadOnlyList<SessionCheckpoint>>());
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        if (!File.Exists(sessionFile))
        {
            _owner.InvalidateMessageCache(sessionId);
            return Task.FromResult(Result.Failure<IReadOnlyList<SessionCheckpoint>>(
                SessionStoreErrors.SessionNotFound(sessionId)));
        }

        // #1749: a session file is read as a whole or not at all. A file in
        // a format generation this build does not read is refused HERE,
        // before the scan, rather than scanned for the checkpoint records of
        // a format this build does not know.
        var generation = SessionFileReader.ReadFormatGeneration(sessionFile, sessionId);
        if (generation.IsFailure)
        {
            return Task.FromResult(generation.ConvertFailure<IReadOnlyList<SessionCheckpoint>>());
        }

        return Result.Try(async () =>
        {
            ct.ThrowIfCancellationRequested();
            // Span locals cannot live in an async method (CS4012), so the
            // byte-level scan is a sync helper; the only await is the lock.
            var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                return (IReadOnlyList<SessionCheckpoint>)ReadCheckpoints(sessionFile, sessionId, _logger);
            }
            finally
            {
                semaphore.Release();
            }
        }, ResultErrors.Message)
            .TapError(e => JsonlSessionStoreLog.ListCheckpointsFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Rewind a session to a checkpoint (#1247): the
    ///     session is truncated to the checkpoint's anchor message, checkpoints
    ///     past the anchor go with the dropped future, and a trail marker is
    ///     appended — the honest trail: which checkpoint was restored, how
    ///     many messages were dropped, when. History is truncated, but the
    ///     rewind itself is recorded, not silent.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#1551 (Memento):</b> this member is the caretaker half of
    ///         the pattern — same as <see cref="CreateCheckpointAsync" />: it
    ///         resolves the file, checks the session still exists, then
    ///         delegates to the originator
    ///         (<see cref="JsonlSessionStore.RestoreCheckpointAsync" />). It performs no
    ///         message scanning and no index arithmetic: the restore knows a
    ///         checkpoint only by its opaque
    ///         <see cref="SessionCheckpointMemento" />, which the originator
    ///         re-materializes from the listed rows through the mapper kit.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="checkpointId">Id of the checkpoint to restore (from <see cref="ListCheckpointsAsync" />).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were dropped and how many remain.</returns>
    public async Task<Result<SessionRewindOutcome>> RewindToCheckpointAsync(
        string sessionId, string checkpointId, CancellationToken ct = default)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return resolved.ConvertFailure<SessionRewindOutcome>();
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        if (!File.Exists(sessionFile))
        {
            _owner.InvalidateMessageCache(sessionId);
            return Result.Failure<SessionRewindOutcome>(SessionStoreErrors.SessionNotFound(sessionId));
        }

        // #1551: restore is originator work — the caretaker knows nothing
        // about anchors, message order or the trail line's internals.
        return await _owner.RestoreCheckpointAsync(sessionId, sessionFile, checkpointId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Append one checkpoint line under the per-session lock and drop the
    ///     parse cache for the session (the file changed). The caller decides
    ///     existence/failure semantics; this only performs the write.
    /// </summary>
    internal async Task<Result> AppendCheckpointEntryAsync(
        string sessionId, string sessionFile, CheckpointEntry entry, CancellationToken ct)
    {
        return await Result.Try(async () =>
        {
            var semaphore = await _owner.GetSessionLockAsync(sessionId, ct).ConfigureAwait(false);
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                byte[] line = SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.CheckpointEntry);
                await File.AppendAllBytesAsync(sessionFile, line, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }

            _owner.InvalidateMessageCache(sessionId);
        }, ResultErrors.Message)
            .TapError(e => JsonlSessionStoreLog.AppendCheckpointFailed(_logger, sessionId, e));
    }

    /// <summary>
    ///     Sync byte-level scan of one session file for checkpoint lines.
    ///     Sync on purpose: the verdicts run over <c>ReadOnlySpan&lt;byte&gt;</c>
    ///     locals (CS4012), mirroring <see cref="SessionFileIO.Scan" />.
    /// </summary>
    private static List<SessionCheckpoint> ReadCheckpoints(
        string sessionFile, string sessionId, ILogger logger)
    {
        var checkpoints = new List<SessionCheckpoint>();
        using var source = new FileStream(
            sessionFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        using var reader = new ChunkedLineReader(source);
        while (reader.Fill())
        {
            while (reader.TryGetRecord(out var record))
            {
                FoldCheckpointRecord(record, sessionId, checkpoints, logger);
            }
        }

        if (reader.TryGetTrailingRecord(out var trailing))
        {
            FoldCheckpointRecord(trailing, sessionId, checkpoints, logger);
        }

        return checkpoints;
    }

    /// <summary>
    ///     Fold one record into <paramref name="checkpoints" /> when it is a
    ///     well-formed checkpoint line; anything else is not this method's
    ///     business. A corrupt checkpoint line warns and is skipped (§ROP-001).
    /// </summary>
    private static void FoldCheckpointRecord(
        ReadOnlySpan<byte> record, string sessionId, List<SessionCheckpoint> checkpoints, ILogger logger)
    {
        if (!SessionFileReader.IsCheckpointLine(record))
        {
            return;
        }

        CheckpointEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize(record, JsonlCodecContext.Default.CheckpointEntry);
        }
        catch (JsonException ex)
        {
            JsonlSessionStoreLog.SkippingMalformedCheckpointLine(logger, ex, sessionId);
            return;
        }

        if (entry is not { Type: "checkpoint", Id: { Length: > 0 }, MessageId: { Length: > 0 } })
        {
            JsonlSessionStoreLog.CheckpointLineMissingIds(logger, sessionId);
            return;
        }

        // #1551: row → contract goes through the mapper kit — no second
        // converter of the same border may exist.
        checkpoints.Add(SessionCheckpointMapperKit.ToContract(entry, sessionId));
    }
}
