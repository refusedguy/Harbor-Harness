using System.Threading.Channels;
using Harbor.Abstractions.Models;
namespace Harbor.Abstractions.Sessions;
// The default bodies of the #1784 members are written as ONE-LINE blocks on
// purpose. That was a workaround for the line-based public-API scanner
// (tools/check-public-api-abstractions.py, deleted in #1845): it read an
// interface body line by line, and a default body split across lines was
// read as an extra member, so the frozen baseline grew a row that said
// nothing. The scanner is gone — the surface is read off the compiler's
// symbol table now — but the one-line shape stays, because a member per
// line is what a reader of a contract wants to see, and the comment is kept
// honest rather than left to describe a tool that is not in the tree.
/// <summary>
///     Storage abstraction for sessions (Repository pattern, GOF).
///     Implementations: JSONL (default), SQLite (opt), Postgres (future plugin).
/// </summary>
/// <remarks>
///     <para>
///         The session store is the durable backing store for conversations. It owns session
///         metadata (CRUD via <see cref="CreateAsync" />, <see cref="GetAsync" />, <see cref="ListAsync" />,
///         <see cref="DeleteAsync" />), message history (<see cref="AppendMessageAsync" />,
///         <see cref="UpdateMessageAsync" />, <see cref="GetMessagesAsync" />), and aggregated stats
///         (<see cref="GetStatsAsync" />, <see cref="UpdateStatsAsync" />).
///     </para>
///     <para>
///         Implementations MUST be thread-safe and MUST persist data across process restarts
///         (except for <c>MemorySessionStore</c> which is for tests only).
///     </para>
///     <para>
///         <b>Failure contract (#199, shared by all three stores):</b> expected
///         states travel the <see cref="Result" /> rail — nothing here throws
///         except <see cref="OperationCanceledException" /> (cancellation
///         propagates, it is never a store failure). Shapes:
///         <list type="bullet">
///             <item>missing session → <c>Session '{id}' not found.</c></item>
///             <item>missing message → <c>Message '{mid}' not found in session '{id}'.</c></item>
///             <item>corrupt/empty file-backed state → names the session, the reason, and the path.</item>
///         </list>
///         Unreadable message rows/lines are skipped with a warning, never a
///         whole-history failure.
///     </para>
///     <para>
///         <b>The log and the surface (#1784).</b> A store keeps a LOG — every
///         record ever written, in write order — and exposes a SURFACE: the
///         folded view of that log, which is what
///         <see cref="GetMessagesAsync" /> returns. The two differ because a
///         rewind takes messages OUT of the surface without deleting them from
///         the log, and because an edited message leaves its previous version
///         in the log as a shadowed record.
///     </para>
///     <para>
///         <b>What the invariant covers.</b> The "the agent's text never
///         disappears" invariant is about the REWIND path and the IN-PLACE
///         EDIT path — the two that physically destroyed text until #1784. It
///         is NOT about compaction: compaction appends a summary message and
///         hides the folded messages from the MODEL CONTEXT only, so the
///         records are in the log before and after it.
///     </para>
///     <para>
///         <b>What is guaranteed where.</b> The invariant is guaranteed by the
///         backends that implement <see cref="GetSessionLogAsync" /> and
///         <see cref="RewindMessagesAfterAsync" />. For a store that has not
///         been migrated, the members below fail loudly rather than deleting
///         anything, and the legacy destructive behaviour stays reachable only
///         through the explicitly deprecated
///         <see cref="DeleteMessagesAfterAsync" />.
///     </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    ///     Create a new session.
    /// </summary>
    /// <param name="directory">The working directory of the session.</param>
    /// <param name="agentName">The agent name to bind.</param>
    /// <param name="providerId">The provider id to bind.</param>
    /// <param name="modelId">The model id to bind.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-created <see cref="Session" />.</returns>
    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default);

    /// <summary>
    ///     Get a session by id.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session, or failure if not found.</returns>
    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     List sessions, optionally filtered by project id.
    /// </summary>
    /// <param name="projectId">Optional project id filter (all projects if null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of matching sessions.</returns>
    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default);

    /// <summary>
    ///     Append a message to a session. Messages are persisted in insertion order.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="message">The message to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default);

    /// <summary>
    ///     Update an existing message in place (e.g. for compaction summaries).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="message">The message to update (matched by id).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session or message does not exist.</returns>
    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default);

    /// <summary>
    ///     Get all messages for a session in chronological order.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of messages, or failure if the session does not exist.</returns>
    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Read the session's TRANSCRIPT: the messages a reader gets — with
    ///     the turns the log never closed closed in memory — together with what
    ///     the read could not deliver (#1744).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="GetMessagesAsync" /> returns the LOG's messages: what
    ///         the store holds, no more. This member returns the READ's view of
    ///         them, and the difference is exactly what a crashed process leaves
    ///         behind:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <term>a broken record</term>
    ///             <description>
    ///                 The bytes of a record are not a complete document — the
    ///                 write was cut. The content is unknowable, so the record is
    ///                 skipped; <see cref="TranscriptIntegrity.UnreadableRecordCount" />
    ///                 says how many, and
    ///                 <see cref="TranscriptIntegrity.IsTailTruncated" /> says
    ///                 whether the cut was at the tail.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>an unclosed turn</term>
    ///             <description>
    ///                 The assistant asked for tools and the results never
    ///                 landed. The transcript closes the turn IN MEMORY (nothing
    ///                 is written back), so a reader sees a balanced history
    ///                 with the interrupted calls marked, and
    ///                 <see cref="TranscriptIntegrity.UnclosedToolCalls" /> names
    ///                 them.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         <b>Default implementation:</b> a failure. A store that cannot
    ///         tell the caller what it could not deliver must say so: a
    ///         transcript that reads as complete when it is not is the exact
    ///         lie this member exists to remove.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The transcript and its integrity, or failure if the session does not
    ///     exist or the store does not expose one.
    /// </returns>
    public Task<Result<SessionTranscript>> GetTranscriptAsync(string sessionId, CancellationToken ct = default)
    { return Task.FromResult(Result.Failure<SessionTranscript>($"Session store '{GetType().Name}' does not expose a transcript (#1744).")); }

    /// <summary>
    ///     Get the session's whole LOG: every record ever written, in write
    ///     order — the current messages, the versions they replaced, and the
    ///     events that moved messages out of the surface.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the read side of the append-only model (#1784). The log
    ///         is the truth; <see cref="GetMessagesAsync" /> returns the
    ///         SURFACE, the fold of that log. A caller that needs to know what
    ///         the agent said BEFORE a rewind, or what a message said before it
    ///         was edited, reads the log.
    ///     </para>
    ///     <para>
    ///         <b>Default implementation:</b> a failure. A store that has not
    ///         been migrated to the append-only log has no log to return, and
    ///         reporting an empty log as if it were the whole truth would be a
    ///         lie that reads like a fact.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The log records in write order, or failure if the session does not
    ///     exist or the store does not expose a log.
    /// </returns>
    public Task<Result<IReadOnlyList<SessionLogEntry>>> GetSessionLogAsync(string sessionId, CancellationToken ct = default)
    { return Task.FromResult(Result.Failure<IReadOnlyList<SessionLogEntry>>($"Session store '{GetType().Name}' does not expose a session log (#1784).")); }

    /// <summary>
    ///     Delete a session and all of its messages.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Update a session's metadata (e.g. title).
    /// </summary>
    /// <param name="session">The updated session record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default);

    /// <summary>
    ///     Get aggregated stats for a session.
    /// </summary>
    /// <remarks>
    ///     Stats semantics diverge by implementation: <c>JsonlSessionStore</c>
    ///     derives stats from the message history on every call (writes via
    ///     <see cref="UpdateStatsAsync" /> are accepted but ignored), while
    ///     <c>SqliteSessionStore</c> and <c>MemorySessionStore</c> return the
    ///     last stored metadata record.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current stats, or failure if the session does not exist.</returns>
    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Update aggregated stats for a session.
    /// </summary>
    /// <remarks>
    ///     <c>JsonlSessionStore</c> derives stats from messages (see
    ///     <see cref="GetStatsAsync" />), so this is a no-op success there;
    ///     the other stores persist the record and fail when the session does
    ///     not exist.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="metadata">The new stats to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default);

    /// <summary>
    ///     Rewind the session to <paramref name="messageId" />: every message
    ///     after the anchor LEAVES THE SURFACE, and the records stay in the log.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the append-only replacement for the old destructive
    ///         <see cref="DeleteMessagesAfterAsync" /> (#1784). The anchor
    ///         itself is KEPT — this is "rewind to here" semantics for session
    ///         revert/branch-from-history — and the tail is not deleted: the
    ///         records stay in the log as shadowed records, and the rewind is
    ///         recorded IN the log as an event.
    ///     </para>
    ///     <para>
    ///         The returned count is how many messages left the surface. It is
    ///         the same number the destructive verb used to report, and it does
    ///         NOT mean that many records were destroyed.
    ///     </para>
    ///     <para>
    ///         <b>Default implementation:</b> a failure. A store that has not
    ///         been migrated must NOT fall back to deleting messages — that
    ///         fallback is exactly the data loss this contract removes, and a
    ///         loud failure is the only honest answer for a store that cannot
    ///         keep the records.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Id of the message to keep as the new tail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The number of messages that left the surface, or failure if the
    ///     session/message does not exist or the store does not support
    ///     append-only rewind.
    /// </returns>
    public Task<Result<int>> RewindMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    { return Task.FromResult(Result.Failure<int>($"Session store '{GetType().Name}' does not support append-only rewind (#1784).")); }

    /// <summary>
    ///     Delete every message after (insertion-order) the given message. The
    ///     target message itself is KEPT — this is "rewind to here" semantics
    ///     for session revert/branch-from-history.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Soft-deprecated (#1784) — use
    ///         <see cref="RewindMessagesAfterAsync" />.</b> This member is kept
    ///         as a FORWARDER, not removed: physical deletion has no place in
    ///         the append-only model, and a rewind now leaves the surface
    ///         without deleting anything from the log.
    ///     </para>
    ///     <para>
    ///         The deprecation is documentation-only — there is deliberately no
    ///         <c>[Obsolete]</c> attribute, because this repository compiles
    ///         with warnings-as-errors and the attribute would break every
    ///         out-of-tree implementor of this interface.
    ///     </para>
    ///     <para>
    ///         A store that still OVERRIDES this member keeps its legacy
    ///         destructive behaviour until it is migrated; the default
    ///         implementation forwards to
    ///         <see cref="RewindMessagesAfterAsync" />, so a migrated store
    ///         answers this call correctly without overriding it.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Id of the message to keep as the new tail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of deleted messages, or failure if session/message not found.</returns>
    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    { return RewindMessagesAfterAsync(sessionId, messageId, ct); }

    /// <summary>
    ///     Physically remove the messages after <paramref name="messageId" />
    ///     from the log — the ONE operation that destroys records.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Prune is NEVER automatic (#1784, owner decision). An implicit
    ///         prune would silently destroy exactly what the append-only log
    ///         exists to keep, so the only path that removes records is an
    ///         explicit command that says how much it is about to destroy.
    ///     </para>
    ///     <para>
    ///         <b>Default implementation:</b> a failure. A store that has not
    ///         been migrated has no prune plan, and defaulting to "nothing was
    ///         pruned" would report success for work never done.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Id of the message to keep as the new tail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The number of records removed, or failure if the session/message
    ///     does not exist or the store does not support prune.
    /// </returns>
    public Task<Result<int>> PurgeMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
    { return Task.FromResult(Result.Failure<int>($"Session store '{GetType().Name}' does not support explicit prune (#1784).")); }
}

/// <summary>
///     Context for a session in agent loop.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ISessionContext" /> is the per-run mutable view of a session. It exposes the
///         session record, an in-memory snapshot of the message history, a steering channel for
///         injecting out-of-band messages, and helpers to append new messages / update usage stats.
///     </para>
///     <para>
///         Implementations are NOT thread-safe for the message list — only the agent loop reads it.
///         The steering queue (<see cref="Channel{T}" />) is thread-safe for writers.
///     </para>
/// </remarks>
public interface ISessionContext
{
    /// <summary>
    ///     The session record.
    /// </summary>
    public Session Session { get; }

    /// <summary>
    ///     In-memory snapshot of the message history. Updated as the loop appends messages.
    /// </summary>
    public IReadOnlyList<AgentMessage> Messages { get; }

    /// <summary>
    ///     Bounded channel for steering messages injected mid-run via <see cref="IAgent.Steer" />.
    /// </summary>
    public Channel<AgentMessage> SteeringQueue { get; }

    /// <summary>
    ///     Append a message to the session (both the in-memory snapshot and the durable store).
    /// </summary>
    /// <param name="message">The message to append.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default);

    /// <summary>
    ///     Make the session's state durable and report whether it is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The durability checkpoint (#1765), fail-closed.</b> A
    ///         checkpoint answers one question: "does the session's state
    ///         survive a crash?". When the answer is no, the work that depends
    ///         on that state must NOT start: a tool body with a side effect, a
    ///         model request whose answer would be lost, the next agent step.
    ///     </para>
    ///     <para>
    ///         The context is the right place for the answer because it is the
    ///         only thing that knows the half that matters: whether the records
    ///         it appended actually reached the store. A context that wrote a
    ///         record to memory and lost it on disk has a state that survives
    ///         nothing, and no caller can see that from the outside.
    ///     </para>
    ///     <para>
    ///         <b>Default implementation:</b> a FAILURE, loudly. A context
    ///         that cannot say whether its state survives a crash must not
    ///         report that it does, because the caller would start work that the log
    ///         cannot record, which is the exact gap this member closes. The
    ///         omission is visible: the run stops and names the reason, which
    ///         is the direction the repository's default-member convention
    ///         requires (see <c>docs/PATTERNS.md</c> §"Default interface
    ///         members").
    ///     </para>
    /// </remarks>
    /// <param name="reason">
    ///     What the checkpoint protects, for example "before the model request", "before
    ///     the tool body". It rides the failure text, so a reader of the log
    ///     learns which boundary was closed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success when the session's state is durable; failure naming the
    ///     reason when it is not, and the dependent work must not start.
    /// </returns>
    public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Failure(
            $"Session context '{GetType().Name}' does not expose a durability checkpoint (#1765)."));
    }

    /// <summary>
    ///     Add the usage from one LLM call to the session's aggregated stats,
    ///     priced with that call's model rates, and publish the new total as a
    ///     <see cref="Events.SessionStatsEvent" />.
    /// </summary>
    /// <remarks>
    ///     #653: the caller passes <paramref name="pricing" /> because the model
    ///     is resolved in the core (the agent loop resolved it to make the call),
    ///     and a model is the only thing that carries rates. The context folds
    ///     the delta and publishes; the presentation layer reads that number and
    ///     computes nothing.
    /// </remarks>
    /// <param name="usage">The usage to add.</param>
    /// <param name="pricing">
    ///     Rates of the model that produced <paramref name="usage" />;
    ///     <see cref="Models.Pricing.Unknown" /> when it publishes none.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default);

    /// <summary>
    ///     The compaction-record seam of this session's store, or null when the
    ///     store records no compaction events (#1767).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A compaction that runs against a store WITH this seam is
    ///         bracketed in the session log: <c>compaction/start</c> is
    ///         appended before the summarizer runs and <c>compaction/end</c>
    ///         after everything the compaction wrote, so a crash in between
    ///         leaves a detectable unmatched start.
    ///     </para>
    ///     <para>
    ///         The default is null: a session context whose store records no
    ///         compaction events says so instead of pretending the bracket is
    ///         there. The omission is visible to the caller, which is the
    ///         direction the default-member convention requires.
    ///     </para>
    /// </remarks>
    public ICompactionEventStore? CompactionEvents => null;
}
