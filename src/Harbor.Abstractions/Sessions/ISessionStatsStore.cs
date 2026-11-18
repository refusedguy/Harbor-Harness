// ISessionStatsStore.cs — the aggregated-stats capability of a session store
// (#2569).
//
// WHY A SEPARATE SEAM
// -------------------
// Interface segregation, the same call the checkpoint axis (#1247), the
// feedback axis (#1746), the compaction axis (#1767) and the operation-event
// axis (#2226) made: ISessionStore stays the persistence contract every
// backend implements, while these two verbs are a capability a backend
// implements when it can expose usage/cost stats. A store that does not
// implement the capability is not forced into members it cannot honour (LSP),
// and a caller can ASK for the seam before it depends on it.
//
// ISessionStore extends this interface, and both members carry default
// bodies that fail loudly. Existing implementors therefore keep compiling,
// and a new backend that only supports CRUD and messages is not dragged into
// the stats semantics — including the three-way divergence the docs below
// record.

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The aggregated-stats capability of a session store: read and update the
///     session's usage/cost record (#2569).
/// </summary>
/// <remarks>
///     <para>
///         The failure contract is the shared one — expected states travel the
///         <see cref="Result" /> rail, and nothing here throws except
///         <see cref="OperationCanceledException" />.
///     </para>
///     <para>
///         Stats semantics diverge by implementation: <c>JsonlSessionStore</c>
///         derives stats from the message history on every call (writes via
///         <see cref="UpdateStatsAsync" /> are accepted but ignored), while
///         <c>SqliteSessionStore</c> and <c>MemorySessionStore</c> return the
///         last stored metadata record. The divergence stays documented here,
///         on the seam that owns it, rather than on the CRUD contract.
///     </para>
/// </remarks>
public interface ISessionStatsStore
{
    /// <summary>
    ///     Get aggregated stats for a session.
    /// </summary>
    /// <remarks>
    ///     <b>Default implementation:</b> a failure. A store that does not
    ///     expose stats must say so: a zeroed record that reads as real usage
    ///     is the exact lie this capability removes.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current stats, or failure if the session does not exist or the store does not expose stats.</returns>
    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
    { return Task.FromResult(Result.Failure<SessionMetadata>($"Session store '{GetType().Name}' does not expose stats (#2569).")); }

    /// <summary>
    ///     Update aggregated stats for a session.
    /// </summary>
    /// <remarks>
    ///     <b>Default implementation:</b> a failure. A store that does not
    ///     persist stats must not report success for a write it never made.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="metadata">The new stats to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist or the store does not expose stats.</returns>
    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
    { return Task.FromResult(Result.Failure($"Session store '{GetType().Name}' does not expose stats (#2569).")); }
}
