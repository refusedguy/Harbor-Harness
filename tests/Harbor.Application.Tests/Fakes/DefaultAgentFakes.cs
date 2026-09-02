using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Tests.Fakes;

public sealed class FakeSessionStore(Session session) : ISessionStore
{
    private readonly object _lock = new();
    private readonly List<AgentMessage> _messages = [];
    private TaskCompletionSource? _gatedAppend;
    private int _appends;

    public int Appends => Volatile.Read(ref _appends);

    /// <summary>Working directory last passed to <see cref="CreateAsync" /> (null = never called).</summary>
    public string? LastCreatedDirectory { get; private set; }

    public void GateNextAppend(TaskCompletionSource gate)
    {
        _gatedAppend = gate;
    }

    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
    {
        LastCreatedDirectory = directory;
        return Task.FromResult(Result.Success(session));
    }

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(sessionId == session.Id
            ? Result.Success(session)
            : Result.Failure<Session>($"Session '{sessionId}' was not found."));

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));

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
            lock (_lock)
            {
                _messages.Add(message);
            }

            return Task.FromResult(Result.Success());
        }

        return AwaitGateThenAppend(gate, message);
    }

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([.. _messages]));
        }
    }

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<int>("DeleteMessagesAfter is not supported by this test fake."));

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(SessionMetadata.Empty));

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

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

public sealed class FakeAgentLoop(Result? runOutcome = null) : IAgentLoop
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _runs);
        return Task.FromResult(runOutcome ?? Result.Success());
    }
}
