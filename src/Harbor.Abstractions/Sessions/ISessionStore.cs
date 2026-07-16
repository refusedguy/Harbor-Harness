using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Sessions;

/// <summary>
/// Storage abstraction for sessions (Repository pattern, GOF).
/// Implementations: JSONL (default), SQLite (opt), Postgres (future plugin).
/// </summary>
/// <remarks>
/// <para>
/// The session store is the durable backing store for conversations. It owns session
/// metadata (CRUD via <see cref="CreateAsync"/>, <see cref="GetAsync"/>, <see cref="ListAsync"/>,
/// <see cref="DeleteAsync"/>), message history (<see cref="AppendMessageAsync"/>,
/// <see cref="UpdateMessageAsync"/>, <see cref="GetMessagesAsync"/>), and aggregated stats
/// (<see cref="GetStatsAsync"/>, <see cref="UpdateStatsAsync"/>).
/// </para>
/// <para>
/// Implementations MUST be thread-safe and MUST persist data across process restarts
/// (except for <c>MemorySessionStore</c> which is for tests only).
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Create a new session.
    /// </summary>
    /// <param name="directory">The working directory of the session.</param>
    /// <param name="agentName">The agent name to bind.</param>
    /// <param name="providerId">The provider id to bind.</param>
    /// <param name="modelId">The model id to bind.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-created <see cref="Session"/>.</returns>
    Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default);

    /// <summary>
    /// Get a session by id.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session, or failure if not found.</returns>
    Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// List sessions, optionally filtered by project id.
    /// </summary>
    /// <param name="projectId">Optional project id filter (all projects if null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of matching sessions.</returns>
    Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default);

    /// <summary>
    /// Append a message to a session. Messages are persisted in insertion order.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="message">The message to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default);

    /// <summary>
    /// Update an existing message in place (e.g. for compaction summaries).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="message">The message to update (matched by id).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session or message does not exist.</returns>
    Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default);

    /// <summary>
    /// Get all messages for a session in chronological order.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of messages, or failure if the session does not exist.</returns>
    Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete a session and all of its messages.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Get aggregated stats for a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current stats, or failure if the session does not exist.</returns>
    Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Update aggregated stats for a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="metadata">The new stats to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default);
}

/// <summary>
/// Context for a session in agent loop.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISessionContext"/> is the per-run mutable view of a session. It exposes the
/// session record, an in-memory snapshot of the message history, a steering channel for
/// injecting out-of-band messages, and helpers to append new messages / update usage stats.
/// </para>
/// <para>
/// Implementations are NOT thread-safe for the message list — only the agent loop reads it.
/// The steering queue (<see cref="Channel{T}"/>) is thread-safe for writers.
/// </para>
/// </remarks>
public interface ISessionContext
{
    /// <summary>
    /// The session record.
    /// </summary>
    Session Session { get; }

    /// <summary>
    /// In-memory snapshot of the message history. Updated as the loop appends messages.
    /// </summary>
    IReadOnlyList<AgentMessage> Messages { get; }

    /// <summary>
    /// Bounded channel for steering messages injected mid-run via <see cref="IAgent.Steer"/>.
    /// </summary>
    System.Threading.Channels.Channel<Models.AgentMessage> SteeringQueue { get; }

    /// <summary>
    /// Append a message to the session (both the in-memory snapshot and the durable store).
    /// </summary>
    /// <param name="message">The message to append.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default);

    /// <summary>
    /// Add the usage from one LLM call to the session's aggregated stats.
    /// </summary>
    /// <param name="usage">The usage to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStatsAsync(Usage usage, CancellationToken ct = default);
}
