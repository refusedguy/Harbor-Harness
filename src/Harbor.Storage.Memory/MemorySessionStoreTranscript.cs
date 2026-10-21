// MemorySessionStoreTranscript.cs — the transcript axis of the in-memory
// store (#1744).
//
// The transcript read lives in its own file for one reason: the store's own
// file is cited LINE BY LINE by the failure-text parity rule
// (tests/Harbor.Architecture.Tests/SessionStoreFailureTextParityRules.cs),
// and a method inserted in the middle of it moves every citation below the
// insertion point. The axis therefore sits beside the store, not inside it —
// the same shape JsonlSessionStore uses for its checkpoint axis.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Shared;

namespace Harbor.Storage.Memory;

public sealed partial class MemorySessionStore
{
    /// <summary>
    ///     Read the session's transcript: the messages a reader gets — with the
    ///     turns the log never closed closed in memory — together with what the
    ///     read could not deliver (#1744).
    /// </summary>
    /// <remarks>
    ///     An in-memory store has no unreadable records: every message it
    ///     holds was handed to it whole. What it can still hold is an unclosed
    ///     turn — a message appended, then the process that appended it died
    ///     before the tool results landed — and that is what the integrity
    ///     reports.
    /// </remarks>
    /// <remarks>
    ///     The transcript describes the SURFACE (#1752): the messages the log
    ///     still shows. A rewind took the tail out of it, and a reader gets
    ///     the surface, so that is what the transcript closes its turns over.
    /// </remarks>
    public Task<Result<SessionTranscript>> GetTranscriptAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(
                Result.Failure<SessionTranscript>(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            var copy = SessionLogFold.Fold(log).Current;
            var unclosed = TranscriptInspector.FindUnclosedToolCalls(copy);
            var integrity = new TranscriptIntegrity(0, false, unclosed);
            var balanced = TranscriptInspector.CloseUnclosedToolCalls(copy);
            return Task.FromResult(
                Result.Success(new SessionTranscript(balanced, integrity)));
        }
    }
}
