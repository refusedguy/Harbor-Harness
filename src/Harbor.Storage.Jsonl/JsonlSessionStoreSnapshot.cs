// JsonlSessionStoreSnapshot.cs — the JSONL store's ONE-GENERATION snapshot (#1785).
//
// WHY A SEPARATE FILE
// -------------------
// The snapshot read is a NEW capability on a store that #1716 reworked and
// #1784's append-only design is about to reshape again. Keeping it in its own
// partial file means this change touches NO line of JsonlSessionStore.cs, so
// the two lines of work cannot collide.
//
// WHAT IT DOES
// -------------
// One pass over the session file, from which BOTH the header and the history
// are taken. The export then assembles every part of its output from that one
// snapshot, so the export cannot mix generations: the stats it carries always
// describe the history it carries.
//
// The read reuses the store's own read path (SessionFileReader), so it
// inherits the machinery that makes a read provable: the length measured on the
// OPEN handle bounds the read, an append that lands mid-read is detected (the
// snapshot is then marked unstable and re-read by the retry policy), and a
// record caught mid-write is skipped rather than half-parsed.

using Harbor.Storage.Shared;

namespace Harbor.Storage.Jsonl;

public sealed partial class JsonlSessionStore
{
    /// <summary>
    ///     Read one session as a SINGLE snapshot: the line-1 header and the
    ///     message history from one pass over the file, with the stats derived
    ///     from that very history.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#1785:</b> the export used to read the session three times
    ///         (the session record, the stats, the messages), each read a
    ///         separate chance to land on a different generation of a file a
    ///         live agent is still writing. This method replaces the three with
    ///         one.
    ///     </para>
    ///     <para>
    ///         It is INTERNAL on purpose: the promise it makes ("these two
    ///         reads must agree") is not expressible in the domain contract
    ///         without freezing the storage API for every backend and every
    ///         plugin over one store's export bug. The exporter asks for the
    ///         snapshot where it can (the JSONL store), and falls back to two
    ///         reads where it cannot.
    ///     </para>
    /// </remarks>
    internal async Task<Result<SessionSnapshot>> ReadSessionSnapshotAsync(
        string sessionId,
        CancellationToken ct)
    {
        // §3.4: observe cancellation BEFORE the existence policy.
        ct.ThrowIfCancellationRequested();
        var resolved = SessionFilePaths.TryResolveSessionFile(_rootDirectory, sessionId);
        if (resolved.IsFailure)
            return resolved.ConvertFailure<SessionSnapshot>();
        string sessionFile = resolved.Value; // guarded: returned above on failure.
        if (!File.Exists(sessionFile))
            return Result.Failure<SessionSnapshot>(SessionStoreErrors.SessionNotFound(sessionId));

        // ONE pass. The header rides along with the history (the reader captures
        // it), so both describe the same file state.
        var read = await SessionFileReader.ParseMessagesFromDiskAsync(sessionFile, sessionId, _logger, ct)
            .ConfigureAwait(false);
        if (read.IsFailure)
            return read.ConvertFailure<SessionSnapshot>();

        var snapshot = read.Value; // guarded: returned above on failure.
        if (snapshot.Header is null)
            return Result.Failure<SessionSnapshot>(
                $"Session '{sessionId}' carries no readable header record: {sessionFile}.");

        // The stats describe the history this snapshot carries — the same fold
        // GetStatsAsync applies, so the exported numbers are the ones the store
        // itself would have reported for this history.
        Session session = Session.Rehydrate(
            id: snapshot.Header.Id,
            projectId: snapshot.Header.ProjectId,
            directory: snapshot.Header.Directory,
            title: snapshot.Header.Title,
            agent: snapshot.Header.Agent,
            model: snapshot.Header.Model,
            providerId: snapshot.Header.ProviderId,
            createdAt: snapshot.Header.CreatedAt,
            updatedAt: SessionFileIO.ResolveUpdatedAt(snapshot.Header, sessionFile),
            metadata: SessionStatsAggregator.Aggregate(snapshot.Messages),
            parentSessionId: snapshot.Header.ParentSessionId,
            status: snapshot.Header.Status,
            gitBranch: snapshot.Header.GitBranch,
            gitIsDirty: snapshot.Header.GitIsDirty,
            kind: snapshot.Header.Kind);

        return Result.Success(new SessionSnapshot(session, snapshot.Messages));
    }
}
