using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Hooks;
using Harbor.Application.Sessions;
using Harbor.Application.Telemetry;
using Harbor.Diagnostics;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     Per-turn compaction behavior (audit v2 §3.5 concern #3), extracted verbatim
///     from the <c>AgentLoop</c> turn body: threshold check → LLM summary →
///     history update → events/metrics, plus the strict-truncation fallback that
///     engages when the summarizer fails.
/// </summary>
/// <remarks>
///     Stateless across turns: the caller (loop) keeps the
///     <c>truncationFallback</c> flag for the run and feeds it back in each turn.
///     Once the flag is set the behavior never retries compaction — the current
///     and every subsequent turn build their request from a strictly reduced tail.
///     <para>
///         PX4 slice 2: an optional <see cref="IHookRunner" /> fires the
///         advisory <c>PreCompact</c> event before the summarizer runs. Null
///         (tests, direct construction) keeps the hooks-free path byte-for-byte.
///     </para>
/// </remarks>
public sealed class CompactionBehavior(
    ICompactionService compaction,
    ITokenTracker tokenTracker,
    IEventBus eventBus,
    IMetrics metrics,
    ILogger logger,
    IHookRunner? hookRunner = null,
    ICompactionView? compactionView = null)
{
    // #1612: the history-shaping seam — the turn pipeline depends on this
    // contract, never on the CompactionPolicy statics directly.
    private readonly ICompactionView _compactionView = compactionView ?? CompactionView.Instance;
    /// <summary>
    ///     Run the pre-turn compaction pass for one turn.
    /// </summary>
    /// <param name="session">The run's session context.</param>
    /// <param name="turnMessages">The compacted view of the history for this turn.</param>
    /// <param name="model">The resolved model (context window drives the check).</param>
    /// <param name="truncationFallback">Whether the fallback is already engaged for this run.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <param name="turn">
    ///     The turn the compaction runs in — the OWNER of the compaction lock
    ///     (#1767). Null for a standalone attempt.
    /// </param>
    /// <returns>The turn's message view and the (possibly newly engaged) fallback flag.</returns>
    public async Task<CompactionOutcome> BeforeTurnAsync(
        ISessionContext session,
        IReadOnlyList<AgentMessage> turnMessages,
        ModelInfo model,
        bool truncationFallback,
        CancellationToken ct,
        int? turn = null)
    {
        // Never retried once the fallback is engaged — the summarizer just
        // failed, so every turn after the failure derives its request from
        // truncation below.
        if (truncationFallback || !tokenTracker.ShouldCompact(turnMessages, model))
        {
            return new CompactionOutcome(turnMessages, truncationFallback);
        }

        using var activity = HarborTelemetry.Source.StartActivity("Compaction");
        CompactionBehaviorLog.CompactionTriggered(logger, session.Session.Id);

        // #1767: THE LOCK. The compaction is a BRACKET in the session log —
        // the start record is appended FIRST and the end record LAST — and a
        // compaction that cannot take the lock does not run at all: its
        // summary would land in the log without the marker that says a
        // compaction was under way, and that is exactly the write the marker
        // exists to forbid.
        ICompactionEventStore? compactionEvents = session.CompactionEvents;
        if (compactionEvents is null)
        {
            // The store records no compaction events: there is no marker
            // vocabulary at all, so the in-process guard is the only
            // exclusion. Said out loud rather than pretended away.
            CompactionBehaviorLog.CompactionLockUnavailable(logger, session.Session.Id);
        }
        else
        {
            Result lockTaken = await TakeCompactionLockAsync(
                compactionEvents, session.Session.Id, turn, ct).ConfigureAwait(false);

            if (lockTaken.IsFailure)
            {
                CompactionBehaviorLog.CompactionLockFailed(logger, session.Session.Id, lockTaken.Error);
                return await PublishFailureAsync(session.Session.Id, lockTaken.Error, turnMessages).ConfigureAwait(false);
            }
        }

        // PX4 slice 2: advisory notification before the history is summarized.
        // Runs first so a hook that observes the shrink sees the pre-summary
        // world; it cannot veto, delay or rewrite the compaction itself.
        if (hookRunner is not null)
        {
            try
            {
                // The session record names the agent that owns it — the
                // behavior has no AgentDefinition of its own, and inventing a
                // second source for that name would drift from the session.
                await hookRunner.RunPreCompactAsync(
                    session.Session.Id, session.Session.Agent, model.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The runner is contracted never to throw on its own; a
                // hooks-subsystem failure must not stop compaction.
                CompactionBehaviorLog.PreCompactHooksFailed(logger, ex, session.Session.Id);
            }
        }

        await eventBus.PublishAsync(new CompactionStartedEvent(session.Session.Id), ct).ConfigureAwait(false);
        // O9/T.6: record the PRE-compaction context size so the drop
        // is observable per transformation, not just via TokensSaved.
        metrics.Histogram(
            "session.context.size", tokenTracker.EstimateTokens(turnMessages),
            new KeyValuePair<string, object?>("context.phase", "pre-compaction"),
            new KeyValuePair<string, object?>("session.id", session.Session.Id));
        Result<CompactionResult> compactionResult =
            await compaction.CompactAsync(session.Session.Id, turnMessages, model, ct).ConfigureAwait(false);

        // Railway Oriented Programming: Match dispatches to the
        // success or failure branch without an explicit
        // `if (result.IsSuccess)` check, making the
        // happy-path/error-path split structural rather than
        // control-flow.
        return await compactionResult.Match(
            async result =>
            {
                await session.AppendMessageAsync(result.SummaryMessage, ct).ConfigureAwait(false);
                // The lock is released AFTER the summary landed: the end
                // record is the last thing the compaction writes, so a crash
                // between the two leaves an unmatched start — the detectable
                // shape of a compaction that did not finish.
                await ReleaseCompactionLockAsync(
                    compactionEvents, session.Session.Id, turn, error: null,
                    result.PrunedMessageCount, result.TokensSaved, ct).ConfigureAwait(false);
                // Recompute so THIS turn's request is already built
                // from the compacted view instead of the overfull
                // pre-compaction history.
                IReadOnlyList<AgentMessage> compacted =
                    _compactionView.MaterializeCompactedView(session.Messages);
                metrics.Histogram(
                    "session.context.size", tokenTracker.EstimateTokens(compacted),
                    new KeyValuePair<string, object?>("context.phase", "post-compaction"),
                    new KeyValuePair<string, object?>("session.id", session.Session.Id));
                await eventBus.PublishAsync(new CompactionCompletedEvent(
                    session.Session.Id,
                    result.Summary,
                    result.PrunedMessageCount,
                    result.TokensSaved,
                    result.Duration), ct).ConfigureAwait(false);
                return new CompactionOutcome(compacted, truncationFallback);
            },
            async error =>
            {
                // F17: a cancelled run surfaces as a compaction
                // Failure, but it is NOT a summarizer failure — engaging the
                // destructive truncation fallback here would irreversibly
                // degrade the session on a plain Esc.
                if (ct.IsCancellationRequested)
                {
                    CompactionBehaviorLog.CompactionCancelled(logger, session.Session.Id);
                    // The lock is released even on a cancelled run, and it is
                    // released with a token that is not the cancelled one: an
                    // unmatched start would read as a crash that never
                    // happened.
                    await ReleaseCompactionLockAsync(
                        compactionEvents, session.Session.Id, turn, "Compaction cancelled.",
                        prunedMessageCount: 0, tokensSaved: 0, ct: CancellationToken.None).ConfigureAwait(false);
                    return new CompactionOutcome(turnMessages, truncationFallback);
                }

                // Never continue silently with a known-invalid
                // (overfull) context: publish the failure and
                // switch to strict tail truncation for this and
                // all subsequent requests.
                CompactionBehaviorLog.CompactionFailed(logger, error);
                // The failure is RECORDED, not swallowed: the end record
                // carries the reason, so "the compaction failed, and here is
                // why" survives the process.
                await ReleaseCompactionLockAsync(
                    compactionEvents, session.Session.Id, turn, error,
                    prunedMessageCount: 0, tokensSaved: 0, ct: CancellationToken.None).ConfigureAwait(false);
                return await PublishFailureAsync(session.Session.Id, error, turnMessages).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Take the compaction lock: read the session's compaction records,
    ///     report an orphaned lock if the log holds one, and append the start
    ///     record (#1767).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An unmatched start in the log is the EVIDENCE of a compaction
    ///         that never finished. It does not block: a session whose
    ///         previous compaction died has to stay usable, and a lock that
    ///         blocks forever is a session that can never compact again. It is
    ///         reported, and carried forward on the new start record so the
    ///         trail survives the next successful compaction.
    ///     </para>
    /// </remarks>
    /// <param name="compactionEvents">The session's compaction-record seam.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="turn">The turn that owns the lock.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the lock is taken; failure when it cannot be.</returns>
    private async Task<Result> TakeCompactionLockAsync(
        ICompactionEventStore compactionEvents,
        string sessionId,
        int? turn,
        CancellationToken ct)
    {
        Result<IReadOnlyList<SessionCompactionEvent>> existing =
            await compactionEvents.GetCompactionEventsAsync(sessionId, ct).ConfigureAwait(false);

        return await existing
            .Bind(events => AppendStartRecordAsync(compactionEvents, sessionId, turn, events, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Append the start record, naming the orphan it supersedes.
    /// </summary>
    /// <param name="compactionEvents">The session's compaction-record seam.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="turn">The turn that owns the lock.</param>
    /// <param name="events">The compaction records the session already holds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the lock is taken; failure when it cannot be.</returns>
    private async Task<Result> AppendStartRecordAsync(
        ICompactionEventStore compactionEvents,
        string sessionId,
        int? turn,
        IReadOnlyList<SessionCompactionEvent> events,
        CancellationToken ct)
    {
        SessionCompactionEvent? orphan = CompactionLock.FindOpenStart(events);
        if (orphan is not null)
        {
            CompactionBehaviorLog.OrphanedCompactionLock(logger, sessionId, orphan.Turn);
        }

        var start = new SessionCompactionEvent
        {
            Kind = SessionCompactionEventKind.Start,
            Turn = turn,
            OccurredAt = DateTimeOffset.UtcNow,
            OrphanedTurn = orphan?.Turn,
        };

        return await compactionEvents.AppendCompactionEventAsync(sessionId, start, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Release the compaction lock: append the end record (#1767).
    /// </summary>
    /// <remarks>
    ///     The end record is the LAST thing the compaction writes, and it
    ///     carries the outcome — the failure text when the compaction failed.
    ///     A lock that cannot be released leaves the unmatched start behind,
    ///     which is the detectable shape; the failure to release is logged so
    ///     the orphan is never a mystery later.
    /// </remarks>
    /// <param name="compactionEvents">The session's compaction-record seam, or null when it has none.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="turn">The turn that owned the lock.</param>
    /// <param name="error">The failure text, or null when the compaction succeeded.</param>
    /// <param name="prunedMessageCount">How many messages the compaction folded.</param>
    /// <param name="tokensSaved">How many tokens the compaction saved.</param>
    /// <param name="ct">Cancellation token for the append.</param>
    private async Task ReleaseCompactionLockAsync(
        ICompactionEventStore? compactionEvents,
        string sessionId,
        int? turn,
        string? error,
        int prunedMessageCount,
        int tokensSaved,
        CancellationToken ct)
    {
        if (compactionEvents is null)
        {
            return;
        }

        var end = new SessionCompactionEvent
        {
            Kind = SessionCompactionEventKind.End,
            Turn = turn,
            Error = error,
            PrunedMessageCount = prunedMessageCount,
            TokensSaved = tokensSaved,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        Result released = await compactionEvents.AppendCompactionEventAsync(sessionId, end, ct).ConfigureAwait(false);
        if (released.IsFailure)
        {
            CompactionBehaviorLog.CompactionLockReleaseFailed(logger, sessionId, released.Error);
        }
    }

    /// <summary>Publish the failure event (awaited — never fire-and-forget) and engage the fallback.</summary>
    private async Task<CompactionOutcome> PublishFailureAsync(
        string sessionId, string error, IReadOnlyList<AgentMessage> turnMessages)
    {
        await eventBus.PublishAsync(new CompactionFailedEvent(sessionId, error), CancellationToken.None)
            .ConfigureAwait(false);
        return new CompactionOutcome(turnMessages, TruncationFallback: true);
    }
}

/// <summary>Per-turn compaction outcome handed back to the loop.</summary>
/// <param name="TurnMessages">The message view the turn's request is built from.</param>
/// <param name="TruncationFallback">Whether strict truncation is engaged for the rest of the run.</param>
public sealed record CompactionOutcome(IReadOnlyList<AgentMessage> TurnMessages, bool TruncationFallback);

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="CompactionBehavior" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class CompactionBehaviorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Compaction triggered for session {SessionId}")]
    public static partial void CompactionTriggered(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "PreCompact hooks failed for session {SessionId}")]
    public static partial void PreCompactHooksFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Compaction cancelled for session {SessionId}; no fallback")]
    public static partial void CompactionCancelled(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Compaction failed: {Error}. Falling back to truncation.")]
    public static partial void CompactionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "The session store of session {SessionId} records no compaction events; the compaction lock is in-process only (#1767)")]
    public static partial void CompactionLockUnavailable(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Could not take the compaction lock of session {SessionId}: {Error}. The compaction does not run, so nothing is written to the log without its marker.")]
    public static partial void CompactionLockFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Session {SessionId} carries an orphaned compaction lock (turn {Turn}): a previous compaction never released it. The session stays usable; the orphan is recorded (#1767)")]
    public static partial void OrphanedCompactionLock(ILogger logger, string sessionId, int? turn);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Could not release the compaction lock of session {SessionId}: {Error}. The log keeps an unmatched start, which is the detectable shape of a compaction that did not finish.")]
    public static partial void CompactionLockReleaseFailed(ILogger logger, string sessionId, string error);

}
