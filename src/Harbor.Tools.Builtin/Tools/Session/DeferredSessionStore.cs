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
public sealed class DeferredSessionStore : ISessionStore, ISessionGoalStore
{
    private volatile ISessionStore? _inner;

    /// <summary>Wire in the real store once the container can build it.</summary>
    public void Attach(ISessionStore inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private ISessionStore? Current => _inner;

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
    public Task<Result<SessionTranscript>> GetTranscriptAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.GetTranscriptAsync(sessionId, ct)
            : Task.FromResult(Result.Failure<SessionTranscript>(NotReady));
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

    /// <inheritdoc />
    /// <remarks>
    ///     Forwarded, not inherited: the interface's default answer for a
    ///     store that has not been migrated is a loud failure, and this
    ///     forwarder must not turn a store that CAN rewind into one that
    ///     cannot (#1752).
    /// </remarks>
    public Task<Result<IReadOnlyList<SessionLogEntry>>> GetSessionLogAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.GetSessionLogAsync(sessionId, ct)
            : Task.FromResult(Result.Failure<IReadOnlyList<SessionLogEntry>>(NotReady));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Forwarded, not inherited — see
    ///     <see cref="GetSessionLogAsync" />.
    /// </remarks>
    public Task<Result<int>> RewindMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.RewindMessagesAfterAsync(sessionId, messageId, ct)
            : Task.FromResult(Result.Failure<int>(NotReady));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Forwarded, not inherited — see
    ///     <see cref="GetSessionLogAsync" />.
    /// </remarks>
    public Task<Result<int>> PurgeMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        var current = Current;
        return current is not null
            ? current.PurgeMessagesAfterAsync(sessionId, messageId, ct)
            : Task.FromResult(Result.Failure<int>(NotReady));
    }

    /// <summary>
    ///     The goal of the session, forwarded to the real store when it keeps
    ///     goals (#1761).
    /// </summary>
    /// <remarks>
    ///     The goal axis is OPTIONAL on the store, and this forwarder is the
    ///     tools' window onto the store — so it forwards the axis the same way
    ///     it forwards every other member. A backend that keeps no goals
    ///     answers "this session has no goal", which is the honest reading for
    ///     a store that has never heard of one.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The goal, or none when the session has none.</returns>
    public Task<Result<Maybe<Goal>>> GetGoalAsync(string sessionId, CancellationToken ct = default)
    {
        var current = Current;
        if (current is null)
        {
            return Task.FromResult(Result.Failure<Maybe<Goal>>(NotReady));
        }

        return current is ISessionGoalStore goals
            ? goals.GetGoalAsync(sessionId, ct)
            : Task.FromResult(Result.Success(Maybe<Goal>.None));
    }

    /// <summary>
    ///     Append a new revision of the session's goal, forwarded to the real
    ///     store when it keeps goals (#1761).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="goal">The goal revision to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended goal, or failure when the backend keeps no goals.</returns>
    public Task<Result<Goal>> AppendGoalAsync(string sessionId, Goal goal, CancellationToken ct = default)
    {
        var current = Current;
        if (current is null)
        {
            return Task.FromResult(Result.Failure<Goal>(NotReady));
        }

        return current is ISessionGoalStore goals
            ? goals.AppendGoalAsync(sessionId, goal, ct)
            : Task.FromResult(Result.Failure<Goal>(NoGoalAxis));
    }

    private const string NotReady =
        "Session runtime is not initialized yet (host composition incomplete).";

    /// <summary>
    ///     The answer a backend that keeps no goals gives: the goal axis is
    ///     optional, and a store that has never heard of a goal cannot have
    ///     one recorded against it.
    /// </summary>
    private const string NoGoalAxis = "This session backend keeps no goals.";
}
