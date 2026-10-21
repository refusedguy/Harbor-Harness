using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Agents;

/// <summary>
///     Default session context implementation.
/// </summary>
/// <remarks>
///     <para>
///         <b>#653 — the running cost lives here, on the core side.</b> The core
///         knows which model it just called and therefore what that call's
///         <see cref="Usage" /> cost, so the fold happens here and the total
///         leaves as a <see cref="SessionStatsEvent" />. A renderer displays that
///         number and never forms one of its own — see
///         <c>tests/Harbor.Architecture.Tests/CostPricedInCoreRules.cs</c>.
///     </para>
///     <para>
///         <b>Why the total is seeded once, here, instead of re-read per turn.</b>
///         <see cref="ISessionStore.GetStatsAsync" /> diverges by implementation
///         and <c>JsonlSessionStore</c> derives its fold from the message
///         history — which already contains the assistant message this turn just
///         appended. Re-reading per turn and adding the delta on top counts that
///         turn twice. The seed is taken before the first turn, so the running
///         total kept here is exact.
///     </para>
/// </remarks>
internal sealed class DefaultSessionContext : ISessionContext
{
    private readonly List<AgentMessage> _messages;
    private readonly ISessionStore _store;
    private readonly IEventBus? _eventBus;
    private readonly ILogger _logger;

    /// <summary>
    ///     Session-cumulative usage and cost, seeded from the store before the
    ///     first turn (see the type remarks) and folded once per turn. This is
    ///     the value the core publishes; the store's own record is a separate,
    ///     best-effort mirror.
    /// </summary>
    private SessionMetadata _stats;

    /// <summary>
    ///     #1765: why the session's state stopped being durable, once it did.
    ///     <para>
    ///         The append below adds to memory FIRST and then writes to the
    ///         store. When the write fails, the two diverge: memory holds a
    ///         record the log does not, and a run that continues on that memory
    ///         produces effects nobody can read back. The failure used to be
    ///         logged and forgotten, which is exactly the gap the durability
    ///         checkpoint closes, so the cause is kept here and reported by
    ///         every checkpoint that follows.
    ///     </para>
    ///     <para>
    ///         The FIRST cause wins: a later success does not repair the
    ///         record that was lost, so the debt stays until the run ends.
    ///     </para>
    /// </summary>
    private string? _durabilityFailure;

    /// <summary>
    ///     Guards <see cref="_durabilityFailure" />. The append path and the
    ///     checkpoint path are both on the run's thread, but the field is
    ///     written from one and read from another, and a torn read of a
    ///     reference is not a thing .NET does. This is about the ORDER of
    ///     the write and the read, not about atomicity.
    /// </summary>
    private readonly object _durabilityGate = new();

    private DefaultSessionContext(
        Session session,
        IReadOnlyList<AgentMessage> messages,
        ISessionStore store,
        Channel<AgentMessage> steeringQueue,
        IEventBus? eventBus,
        SessionMetadata seed,
        ILogger? logger = null)
    {
        Session = session;
        // Pre-size the internal list to the known count. The previous `.ToList()` always
        // allocated a new List<T> with a default capacity then grew it via doubling.
        _messages = new List<AgentMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            _messages.Add(messages[i]);
        }
        _store = store;
        _eventBus = eventBus;
        _logger = logger ?? NullLogger.Instance;
        _stats = seed;
        SteeringQueue = steeringQueue;
    }

    /// <summary>
    ///     Build a context for one run, seeding its running total from the store.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The seed read is best-effort: a store that cannot answer (closed,
    ///         IO failure) starts the run from <see cref="SessionMetadata.Empty" />
    ///         and logs, rather than failing a prompt the user already sent.
    ///     </para>
    ///     <para>
    ///         <paramref name="eventBus" /> is <see langword="null" /> for a
    ///         session nobody is watching (a sub-agent run): its usage is still
    ///         folded and persisted, it just has no live audience to publish to.
    ///     </para>
    /// </remarks>
    internal static async Task<DefaultSessionContext> CreateAsync(
        Session session,
        IReadOnlyList<AgentMessage> messages,
        ISessionStore store,
        Channel<AgentMessage> steeringQueue,
        IEventBus? eventBus,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(store);

        SessionMetadata seed = await ReadSeedAsync(store, session.Id, logger, ct).ConfigureAwait(false);
        return new DefaultSessionContext(
            session, messages, store, steeringQueue, eventBus, seed, logger);
    }

    /// <summary>
    ///     The run's starting totals: whatever the store already knows, or zero.
    /// </summary>
    private static async Task<SessionMetadata> ReadSeedAsync(
        ISessionStore store,
        string sessionId,
        ILogger? logger,
        CancellationToken ct)
    {
        var stats = await store.GetStatsAsync(sessionId, ct).ConfigureAwait(false);
        if (stats.IsFailure)
        {
            // §4.6-ok: a store that cannot answer must not fail a prompt the user
            // already sent. The run starts from zero and the loss is logged.
            DefaultSessionContextLog.SessionSeedReadFailed((logger ?? NullLogger.Instance), sessionId, stats.Error);
            return SessionMetadata.Empty;
        }

        return stats.Value;
    }

    public Session Session { get; }

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public Channel<AgentMessage> SteeringQueue
    {
        get;
    }

    public async Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        // Memory-first by design (F14 detail): the store append below must not
        // lose its Result silently — a failed write means disk diverges from
        // memory/model, so surface it in the log keyed by session.
        _messages.Add(message);
        Result persisted = await _store.AppendMessageAsync(Session.Id, message, ct).ConfigureAwait(false);
        if (persisted.IsFailure) // §4.6-ok: best-effort persist, ветка только логирует (F14 detail).
        {
            // #1765: the write did not land, so the state this run is about to
            // act on does not survive a crash. The cause is kept and reported
            // by every checkpoint from here on. The append itself stays
            // best-effort (the record is in memory and the run continues), but
            // the WORK that depends on it no longer starts.
            RecordDurabilityFailure(persisted.Error);
            DefaultSessionContextLog.PersistMessageFailed(_logger, message.Id, Session.Id, persisted.Error);
        }
    }

    /// <summary>
    ///     The durability checkpoint (#1765): make the session's state durable
    ///     and report whether it is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One question, one answer: did every record appended so far
    ///         actually reach the store? A context that lost a record has a
    ///         state that survives nothing, and the store cannot know that:
    ///         the record left through this context's append.
    ///     </para>
    ///     <para>
    ///         The failure text names the boundary that was protected and the
    ///         cause, because the caller refuses work on it and the user reads
    ///         the refusal.
    ///     </para>
    /// </remarks>
    /// <param name="reason">What the checkpoint protects.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the state is durable; failure otherwise.</returns>
    public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default)
    {
        string? debt = DurabilityFailure;
        if (debt is not null)
        {
            DefaultSessionContextLog.DurabilityCheckpointFailed(_logger, Session.Id, reason, debt);
            return Task.FromResult(Result.Failure(CheckpointFailure(reason, debt)));
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Keep the FIRST cause. A later append that succeeds does not repair
    ///     the record that was lost, so the debt does not expire.
    /// </summary>
    /// <param name="cause">Why the state stopped being durable.</param>
    private void RecordDurabilityFailure(string cause)
    {
        lock (_durabilityGate)
        {
            _durabilityFailure ??= cause;
        }
    }

    /// <summary>
    ///     Why the state stopped being durable, or null when it still is.
    /// </summary>
    private string? DurabilityFailure
    {
        get
        {
            lock (_durabilityGate)
            {
                return _durabilityFailure;
            }
        }
    }

    /// <summary>
    ///     The refusal text a checkpoint hands to the work it protects.
    /// </summary>
    /// <param name="reason">What the checkpoint protects.</param>
    /// <param name="cause">Why the state is not durable.</param>
    /// <returns>A sentence that says what failed and what did not happen.</returns>
    private static string CheckpointFailure(string reason, string cause) =>
        $"Session log durability checkpoint failed ({reason}): {cause}. "
        + "The session log does not survive a crash, so work that depends on it did not start.";

    public async Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default)
    {
        // 1. Fold the delta into the running total and publish it. The core is
        //    the only place this number is formed: `pricing` is the resolved
        //    ModelInfo's own rate table, so cached tokens bill at their cache
        //    rates and a model with no published price publishes
        //    IsCostKnown=false instead of a $0.0000 that reads as "free".
        _stats = _stats.AddUsage(usage, pricing);
        if (_eventBus is not null)
        {
            await _eventBus.PublishAsync(new SessionStatsEvent(Session.Id, _stats), ct).ConfigureAwait(false);
        }

        // 2. Mirror the delta into the store's own record, best-effort. This is a
        //    read-modify-write against the store's snapshot rather than a write of
        //    `_stats`, so a concurrent sub-agent cost propagation
        //    (SubAgentRunner) landing between two turns is not rolled back.
        //    #1105: that read-modify-write is shared with the propagation, so
        //    it runs under the same per-session stats gate — otherwise a turn
        //    debit landing mid-propagation (or vice versa) is silently lost.
        var gate = SubAgentRunner.StatsGateFor(Session.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stats = await _store.GetStatsAsync(Session.Id, ct).ConfigureAwait(false);
            if (stats.IsFailure)
            {
                // §4.6-ok + rop-final-mile N3: best-effort usage update — store
                // closed / IO failure loses this delta; log instead of the old
                // no-op discard so the loss is observable.
                DefaultSessionContextLog.SessionStatsReadFailed(_logger, Session.Id, stats.Error);
                return;
            }

            var updated = stats.Value.AddUsage(usage, pricing);
            await _store.UpdateStatsAsync(Session.Id, updated, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="DefaultSessionContext" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class DefaultSessionContextLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to read session stats for {SessionId}; usage totals start at zero: {Error}")]
    public static partial void SessionSeedReadFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to persist message {MessageId} for session {SessionId}: {Error}")]
    public static partial void PersistMessageFailed(ILogger logger, string messageId, string sessionId, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to read session stats for {SessionId}: {Error}")]
    public static partial void SessionStatsReadFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Durability checkpoint failed for session {SessionId} ({Reason}): {Error}")]
    public static partial void DurabilityCheckpointFailed(ILogger logger, string sessionId, string reason, string error);

}
