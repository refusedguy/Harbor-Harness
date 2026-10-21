using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
// #887: the canonical failure texts. Linked source (this project has no
// <Compile Include> for the other two files in that folder, and does not need
// them — see the csproj for why). Before this, every not-found sentence below
// was written out by hand: nine SessionNotFound and two MessageNotFound.
using Harbor.Storage.Shared;
namespace Harbor.Storage.Memory;
/// <summary>
///     In-memory session storage — for tests and ephemeral sessions.
///     Implements Repository pattern (GOF) via ISessionStore.
///     All data is lost on process exit. Use for unit tests or one-shot prompts.
/// </summary>
/// <remarks>
///     <para>
///         <b>#1752 — the store keeps a LOG.</b> Not a list of messages: a list
///         of the CURRENT messages, with the versions they replaced and the
///         events that moved them out of the surface kept beside them. An
///         edit appends a new version; a rewind appends an event. Neither
///         rewrites what is already there, so "what did the agent actually
///         say?" still has an answer after either of them.
///     </para>
///     <para>
///         The surface — what <see cref="GetMessagesAsync" /> hands back — is
///         the fold of that log, by the same shared fold every backend uses.
///     </para>
/// </remarks>
public sealed partial class MemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, List<SessionLogEntry>> _logs = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId,
        CancellationToken ct = default)
    {
        var session = Session.Create(directory, agentName, providerId, modelId);
        _sessions[session.Id] = session;
        _logs[session.Id] = new List<SessionLogEntry>();
        return Task.FromResult(Result.Success(session));
    }

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult(Result.Success(session));
        return Task.FromResult(
            Result.Failure<Session>(SessionStoreErrors.SessionNotFound(sessionId)));
    }

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        var sessions = _sessions.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(projectId))
            sessions = sessions.Where(s => s.ProjectId == projectId);
        var ordered = sessions.OrderByDescending(s => s.UpdatedAt).ToList();
        return Task.FromResult(Result.Success<IReadOnlyList<Session>>(ordered));
    }

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            log.Add(new SessionLogEntry
            {
                Kind = SessionLogEntryKind.Message,
                Id = message.Id,
                Position = log.Count,
                Message = message,
            });
        }

        Touch(sessionId);

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Append a NEW VERSION of a message (#1752). The version that was
    ///     there before stays in the log: an edit is a new record, not a
    ///     rewrite, so the text the agent produced before the edit is still
    ///     there to be read.
    /// </summary>
    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            bool known = false;
            for (int i = 0; i < log.Count; i++)
            {
                SessionLogEntry entry = log[i];
                if (entry.Kind == SessionLogEntryKind.Message
                    && string.Equals(entry.Id, message.Id, StringComparison.Ordinal))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
                return Task.FromResult(
                    Result.Failure(SessionStoreErrors.MessageNotFound(sessionId, message.Id)));

            log.Add(new SessionLogEntry
            {
                Kind = SessionLogEntryKind.Message,
                Id = message.Id,
                Position = log.Count,
                Message = message,
            });
        }

        Touch(sessionId);

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     The surface: the messages the log still shows — the current version
    ///     of every message, minus the ones a rewind took out of it.
    /// </summary>
    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(
                Result.Failure<IReadOnlyList<AgentMessage>>(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            var copy = SessionLogFold.Fold(log).Current;
            return Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>(copy));
        }
    }

    /// <summary>
    ///     The whole log: every record the session holds, in write order —
    ///     the current messages, the versions they replaced, and the rewind
    ///     events that moved messages out of the surface (#1752).
    /// </summary>
    public Task<Result<IReadOnlyList<SessionLogEntry>>> GetSessionLogAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(
                Result.Failure<IReadOnlyList<SessionLogEntry>>(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            var copy = new List<SessionLogEntry>(log);
            return Task.FromResult(Result.Success<IReadOnlyList<SessionLogEntry>>(copy));
        }
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_sessions.ContainsKey(sessionId))
            return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));
        _sessions.TryRemove(sessionId, out _);
        _logs.TryRemove(sessionId, out _);
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Rewind to a message: the tail after the anchor leaves the SURFACE,
    ///     and the records stay in the log (#1752).
    /// </summary>
    /// <remarks>
    ///     The rewind is an EVENT in the log — the same record the file-backed
    ///     store writes — so the two backends fold the same log the same way.
    /// </remarks>
    public Task<Result<int>> RewindMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(Result.Failure<int>(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            bool anchorFound = false;
            for (int i = 0; i < log.Count; i++)
            {
                SessionLogEntry entry = log[i];
                if (entry.Kind == SessionLogEntryKind.Message
                    && string.Equals(entry.Id, messageId, StringComparison.Ordinal))
                {
                    anchorFound = true;
                    break;
                }
            }

            if (!anchorFound)
                return Task.FromResult(Result.Failure<int>(
                    SessionStoreErrors.MessageNotFound(sessionId, messageId)));

            int before = SessionLogFold.Fold(log).Current.Count;

            log.Add(new SessionLogEntry
            {
                Kind = SessionLogEntryKind.Rewind,
                Id = Guid.NewGuid().ToString("N"),
                Position = log.Count,
                AnchorId = messageId,
                OccurredAt = DateTimeOffset.UtcNow,
            });

            int after = SessionLogFold.Fold(log).Current.Count;
            int removed = before - after;

            Touch(sessionId);

            return Task.FromResult(Result.Success(removed < 0 ? 0 : removed));
        }
    }

    /// <summary>
    ///     Prune: physically remove the messages after the anchor from the log
    ///     (#1752).
    /// </summary>
    /// <remarks>
    ///     The ONE operation that destroys records, and it is never automatic:
    ///     a caller has to ask for it by name. A rewind writes an event and
    ///     leaves the records alone.
    /// </remarks>
    public Task<Result<int>> PurgeMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
            return Task.FromResult(Result.Failure<int>(SessionStoreErrors.SessionNotFound(sessionId)));

        lock (log)
        {
            // The anchor is the FIRST record of that id — the place the
            // message was written, which is the place it holds in the
            // conversation.
            int anchorIndex = -1;
            for (int i = 0; i < log.Count; i++)
            {
                SessionLogEntry entry = log[i];
                if (entry.Kind == SessionLogEntryKind.Message
                    && string.Equals(entry.Id, messageId, StringComparison.Ordinal))
                {
                    anchorIndex = i;
                    break;
                }
            }

            if (anchorIndex < 0)
                return Task.FromResult(Result.Failure<int>(
                    SessionStoreErrors.MessageNotFound(sessionId, messageId)));

            int removed = 0;
            for (int i = log.Count - 1; i > anchorIndex; i--)
            {
                SessionLogEntry entry = log[i];
                if (entry.Kind != SessionLogEntryKind.Message)
                {
                    continue;
                }

                log.RemoveAt(i);
                removed++;
            }

            Touch(sessionId);

            return Task.FromResult(Result.Success(removed));
        }
    }

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult(Result.Success(session.Metadata));
        return Task.FromResult(
            Result.Failure<SessionMetadata>(SessionStoreErrors.SessionNotFound(sessionId)));
    }

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _sessions[sessionId] = session with { Metadata = metadata };
            return Task.FromResult(Result.Success());
        }
        return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(sessionId)));
    }

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
    {
        if (_sessions.ContainsKey(session.Id))
        {
            _sessions[session.Id] = session;
            return Task.FromResult(Result.Success());
        }
                return Task.FromResult(Result.Failure(SessionStoreErrors.SessionNotFound(session.Id)));
    }

    public void Clear()
    {
        _sessions.Clear();
        _logs.Clear();
    }

    /// <summary>
    ///     Stamp the session as just active — the same thing a write to the
    ///     file does.
    /// </summary>
    /// <param name="sessionId">The session that was written to.</param>
    private void Touch(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _sessions[sessionId] = session with { UpdatedAt = DateTimeOffset.UtcNow };
        }
    }
}
