using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Storage.Memory;

/// <summary>
/// In-memory session storage — for tests and ephemeral sessions.
/// Implements Repository pattern (GOF) via ISessionStore.
///
/// All data is lost on process exit. Use for unit tests or one-shot prompts.
/// </summary>
public sealed class MemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, List<AgentMessage>> _messages = new();

    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId,
        CancellationToken ct = default)
    {
        var session = Session.Create(directory, agentName, providerId, modelId);
        _sessions[session.Id] = session;
        _messages[session.Id] = new List<AgentMessage>();
        return Task.FromResult(Result.Success(session));
    }

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult(Result.Success(session));
        return Task.FromResult(Result.Failure<Session>($"Session '{sessionId}' not found."));
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
        if (!_messages.TryGetValue(sessionId, out var list))
            return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));

        lock (list)
        {
            list.Add(message);
        }

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _sessions[sessionId] = session with { UpdatedAt = DateTimeOffset.UtcNow };
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        if (!_messages.TryGetValue(sessionId, out var list))
            return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));

        lock (list)
        {
            var idx = list.FindIndex(m => m.Id == message.Id);
            if (idx >= 0)
                list[idx] = message;
            else
                list.Add(message);
        }
        return Task.FromResult(Result.Success());
    }

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_messages.TryGetValue(sessionId, out var list))
            return Task.FromResult(Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found."));

        lock (list)
        {
            var copy = list.OrderBy(m => m.CreatedAt).ToList();
            return Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>(copy));
        }
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryRemove(sessionId, out _);
        _messages.TryRemove(sessionId, out _);
        return Task.FromResult(Result.Success());
    }

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult(Result.Success(session.Metadata));
        return Task.FromResult(Result.Failure<SessionMetadata>($"Session '{sessionId}' not found."));
    }

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _sessions[sessionId] = session with { Metadata = metadata };
            return Task.FromResult(Result.Success());
        }
        return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
    }

    public void Clear()
    {
        _sessions.Clear();
        _messages.Clear();
    }
}
