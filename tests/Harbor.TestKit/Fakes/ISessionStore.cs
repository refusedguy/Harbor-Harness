using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>In-memory session store with optional pre-seeded session and gate support for concurrency tests.</summary>
public sealed class FakeSessionStore(Session? session = null) : ISessionStore
{
    private readonly List<AgentMessage> _messages = [];
    private readonly List<SessionMetadata> _updatedStats = [];
    private readonly object _lock = new();
    private TaskCompletionSource? _gatedAppend;
    private int _appends;

    public int Appends => Volatile.Read(ref _appends);

    public string? LastCreatedDirectory { get; private set; }

    public void GateNextAppend(TaskCompletionSource gate)
    {
        lock (_lock)
        {
            _gatedAppend = gate;
        }
    }

    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
    {
        LastCreatedDirectory = directory;
        Session created = session ?? Session.Create(directory, agentName, providerId, modelId);
        return Task.FromResult(Result.Success(created));
    }

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        if (session is null) return Task.FromResult(Result.Failure<Session>("No session configured."));
        return session.Id == sessionId
            ? Task.FromResult(Result.Success(session))
            : Task.FromResult(Result.Failure<Session>($"Session '{sessionId}' was not found."));
    }

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        if (session is null) return Task.FromResult(Result.Success<IReadOnlyList<Session>>([]));
        return Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));
    }

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _appends);
        TaskCompletionSource? gate;
        lock (_lock)
        {
            gate = _gatedAppend;
            _gatedAppend = null;
        }

        if (gate is null)
        {
            lock (_lock) { _messages.Add(message); }
            return Task.FromResult(Result.Success());
        }

        return AwaitGateThenAppend(gate, message);
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_lock) { return Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([.. _messages])); }
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(SessionMetadata.Empty));

    /// <summary>
    ///     Every stats record the core persisted, in order. <c>GetStatsAsync</c>
    ///     deliberately reports <see cref="SessionMetadata.Empty" /> (this fake
    ///     models a store with no aggregate), so a test that needs to see what the
    ///     core WROTE — the priced cost of #653, for one — reads this.
    /// </summary>
    public IReadOnlyList<SessionMetadata> UpdatedStats
    {
        get
        {
            lock (_lock)
            {
                return [.. _updatedStats];
            }
        }
    }

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _updatedStats.Add(metadata);
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));

    private async Task<Result> AwaitGateThenAppend(TaskCompletionSource gate, AgentMessage message)
    {
        await gate.Task.ConfigureAwait(false);
        lock (_lock)
        {
            _messages.Add(message);
        }

        return Result.Success();
    }
}
