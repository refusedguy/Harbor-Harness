// MemorySessionStoreOperation.cs — the in-memory fork/turn crash-marker axis
// (#2226). The memory store is a test backend, so the markers are held in a
// dictionary exactly the way its compaction records are.

using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;

namespace Harbor.Storage.Memory;

public sealed partial class MemorySessionStore
{
    private readonly ConcurrentDictionary<string, List<SessionOperationEvent>> _operations = new();

    public Task<Result> AppendOperationEventAsync(
        string sessionId,
        SessionOperationEvent operationEvent,
        CancellationToken ct = default)
    {
        if (!_sessions.ContainsKey(sessionId))
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));

        List<SessionOperationEvent> events = _operations.GetOrAdd(sessionId, static _ => []);
        lock (events)
        {
            events.Add(operationEvent with { Sequence = events.Count });
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<IReadOnlyList<SessionOperationEvent>>> GetOperationEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (!_sessions.ContainsKey(sessionId))
            return Task.FromResult(Result.Failure<IReadOnlyList<SessionOperationEvent>>(
                SessionStoreErrors.SessionNotFound(sessionId)));

        if (!_operations.TryGetValue(sessionId, out var events))
            return Task.FromResult(Result.Success<IReadOnlyList<SessionOperationEvent>>([]));

        lock (events)
        {
            return Task.FromResult(Result.Success<IReadOnlyList<SessionOperationEvent>>([.. events]));
        }
    }
}
