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
    IHookRunner? hookRunner = null)
{
    /// <summary>
    ///     Run the pre-turn compaction pass for one turn.
    /// </summary>
    /// <param name="session">The run's session context.</param>
    /// <param name="turnMessages">The compacted view of the history for this turn.</param>
    /// <param name="model">The resolved model (context window drives the check).</param>
    /// <param name="truncationFallback">Whether the fallback is already engaged for this run.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The turn's message view and the (possibly newly engaged) fallback flag.</returns>
    public async Task<CompactionOutcome> BeforeTurnAsync(
        ISessionContext session,
        IReadOnlyList<AgentMessage> turnMessages,
        ModelInfo model,
        bool truncationFallback,
        CancellationToken ct)
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
                // Recompute so THIS turn's request is already built
                // from the compacted view instead of the overfull
                // pre-compaction history.
                IReadOnlyList<AgentMessage> compacted =
                    CompactionPolicy.MaterializeCompactedView(session.Messages);
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
            error =>
            {
                // F17: a cancelled run surfaces as a compaction
                // Failure, but it is NOT a summarizer failure — engaging the
                // destructive truncation fallback here would irreversibly
                // degrade the session on a plain Esc.
                if (ct.IsCancellationRequested)
                {
                    CompactionBehaviorLog.CompactionCancelled(logger, session.Session.Id);
                    return Task.FromResult(new CompactionOutcome(turnMessages, truncationFallback));
                }

                // Never continue silently with a known-invalid
                // (overfull) context: publish the failure and
                // switch to strict tail truncation for this and
                // all subsequent requests.
                CompactionBehaviorLog.CompactionFailed(logger, error);
                return PublishFailureAsync(session.Session.Id, error, turnMessages);
            }).ConfigureAwait(false);
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

}
