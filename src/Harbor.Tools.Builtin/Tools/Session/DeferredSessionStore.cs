using Harbor.Abstractions.Sessions;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Forwarding holder used by eager host composition (same gap as
///     <c>DeferredSubAgentRunner</c>): the tool registry is built BEFORE the
///     <see cref="ISessionStore" /> singleton exists, so the host hands the
///     supervision tools this forwarder and attaches the real store once the
///     container can build it. Detached state fails honestly instead of
///     NRE-ing — never a fake read or a dropped steer.
/// </summary>
public sealed class DeferredSessionStore : ISessionStore
{
    private volatile ISessionStore? _inner;

    /// <summary>Wire in the real store once the container can build it.</summary>
    public void Attach(ISessionStore inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private ISessionStore? Current => Volatile.Read(ref _inner);

    /// <inheritdoc />
    public Task<Result<Session>> CreateAsync(
        string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.CreateAsync(directory, agentName, providerId, modelId, ct)
            : Task.FromResult(Result.Failure<Session>(NotReady));
    }

    /// <inheritdoc />
    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.GetAsync(sessionId, ct)
            : Task.FromResult(Result.Failure<Session>(NotReady));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.ListAsync(projectId, ct)
            : Task.FromResult(Result.Failure<IReadOnlyList<Session>>(NotReady));
    }

    /// <inheritdoc />
    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.AppendMessageAsync(sessionId, message, ct)
            : Task.FromResult(Result.Failure(NotReady));
    }

    /// <inheritdoc />
    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.UpdateMessageAsync(sessionId, message, ct)
            : Task.FromResult(Result.Failure(NotReady));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.GetMessagesAsync(sessionId, ct)
            : Task.FromResult(Result.Failure<IReadOnlyList<AgentMessage>>(NotReady));
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.DeleteAsync(sessionId, ct)
            : Task.FromResult(Result.Failure(NotReady));
    }

    /// <inheritdoc />
    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.UpdateAsync(session, ct)
            : Task.FromResult(Result.Failure(NotReady));
    }

    /// <inheritdoc />
    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.GetStatsAsync(sessionId, ct)
            : Task.FromResult(Result.Failure<SessionMetadata>(NotReady));
    }

    /// <inheritdoc />
    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.UpdateStatsAsync(sessionId, metadata, ct)
            : Task.FromResult(Result.Failure(NotReady));
    }

    /// <inheritdoc />
    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.DeleteMessagesAfterAsync(sessionId, messageId, ct)
            : Task.FromResult(Result.Failure<int>(NotReady));
    }

    private const string NotReady =
        "Session runtime is not initialized yet (host composition incomplete).";
}
