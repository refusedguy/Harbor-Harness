// MemorySessionStoreCompaction.cs — the compaction-record axis of the
// in-memory store (#1767).
//
// The axis sits beside the store, not inside it, for the same reason the
// transcript axis does (see MemorySessionStoreTranscript.cs): the store's own
// file is cited by the failure-text parity rule
// (tests/Harbor.Architecture.Tests/SessionStoreFailureTextParityRules.cs),
// and a method inserted in the middle of it moves every citation below the
// insertion point.
//
// WHAT THIS STORE'S LOCK IS
// -------------------------
// An in-memory store has no file to outlive the process, so its compaction
// records are exactly as durable as the store itself: they live as long as the
// process does, and they are what the in-process guard reads to decide
// whether a previous compaction left an orphaned lock. The Memory tier is
// also what the tests run against, so the bracket — start, then the writes of
// the operation, then end — is exercised here first.

using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;

namespace Harbor.Storage.Memory;

// The base list is the store's WHOLE set of seams, and it is repeated here
// verbatim because a partial declaration may not name a different one: the
// store itself names ISessionStore and ICompactionEventStore, and a partial
// type whose parts disagree about their base interfaces does not compile.
public sealed partial class MemorySessionStore : ISessionStore, ICompactionEventStore
{
    /// <summary>
    ///     The compaction records of each session, in write order.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<SessionCompactionEvent>> _compactionEvents = new();

    /// <summary>
    ///     Append one compaction record to the session's log.
    /// </summary>
    /// <remarks>
    ///     The record's position among the session's compaction records is
    ///     assigned here: the list is the log, and the position is its index.
    /// </remarks>
    public Task<Result> AppendCompactionEventAsync(
        string sessionId,
        SessionCompactionEvent compactionEvent,
        CancellationToken ct = default)
    {
        if (!_logs.ContainsKey(sessionId))
        {
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));
        }

        List<SessionCompactionEvent> events = _compactionEvents.GetOrAdd(sessionId, static _ => []);
        lock (events)
        {
            events.Add(compactionEvent with { Sequence = events.Count });
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Read the session's compaction records, in write order.
    /// </summary>
    public Task<Result<IReadOnlyList<SessionCompactionEvent>>> GetCompactionEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (!_logs.ContainsKey(sessionId))
        {
            return Task.FromResult(
                Result.Failure<IReadOnlyList<SessionCompactionEvent>>(SessionStoreErrors.SessionNotFound(sessionId)));
        }

        if (!_compactionEvents.TryGetValue(sessionId, out List<SessionCompactionEvent>? events))
        {
            // A session that never compacted has no records — an empty list,
            // not a failure. "No compaction happened" is a fact.
            return Task.FromResult(Result.Success<IReadOnlyList<SessionCompactionEvent>>([]));
        }

        lock (events)
        {
            return Task.FromResult(Result.Success<IReadOnlyList<SessionCompactionEvent>>([.. events]));
        }
    }
}
