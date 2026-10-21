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
    ///     How many times ONE turn may compact-and-retry after a context
    ///     overflow (#1755).
    /// </summary>
    /// <remarks>
    ///     One covers the common case (the compaction freed the window); the
    ///     second covers a history that overflowed twice over. Beyond that the
    ///     request is refused for a reason compaction does not address, and the
    ///     refusal is what the run reports — the bound is what keeps a history
    ///     that cannot shrink from looping forever.
    /// </remarks>
    public const int MaxOverflowRetries = 2;

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

        Result<IReadOnlyList<AgentMessage>, string> outcome =
            await CompactAndPublishAsync(session, turnMessages, model, ct).ConfigureAwait(false);

        // Railway Oriented Programming: Match dispatches to the
        // success or failure branch without an explicit
        // `if (result.IsSuccess)` check, making the
        // happy-path/error-path split structural rather than
        // control-flow.
        return await outcome.Match(
            compacted => Task.FromResult(new CompactionOutcome(compacted, truncationFallback)),
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

    /// <summary>
    ///     Run one compaction and publish its lifecycle events: the summary is
    ///     appended to the session, the compacted view is materialized, and the
    ///     started/completed events go out.
    /// </summary>
    /// <remarks>
    ///     Both compaction paths go through here — the preventive one
    ///     (<see cref="BeforeTurnAsync" />) and the reactive one
    ///     (<see cref="RecoverFromOverflowAsync" />). The FAILURE is not
    ///     handled here: the two callers disagree about what a failed
    ///     compaction means (the preventive path engages the destructive
    ///     truncation fallback, the reactive one keeps the provider's refusal
    ///     standing), so that decision belongs to the caller.
    /// </remarks>
    private async Task<Result<IReadOnlyList<AgentMessage>, string>> CompactAndPublishAsync(
        ISessionContext session,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct)
    {
        await eventBus.PublishAsync(new CompactionStartedEvent(session.Session.Id), ct).ConfigureAwait(false);
        // O9/T.6: record the PRE-compaction context size so the drop
        // is observable per transformation, not just via TokensSaved.
        metrics.Histogram(
            "session.context.size", tokenTracker.EstimateTokens(messages),
            new KeyValuePair<string, object?>("context.phase", "pre-compaction"),
            new KeyValuePair<string, object?>("session.id", session.Session.Id));

        Result<CompactionResult> compactionResult =
            await compaction.CompactAsync(session.Session.Id, messages, model, ct).ConfigureAwait(false);

        return await compactionResult.Match(
            async result =>
            {
                await session.AppendMessageAsync(result.SummaryMessage, ct).ConfigureAwait(false);
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
                return Result.Success<IReadOnlyList<AgentMessage>, string>(compacted);
            },
            error => Task.FromResult(Result.Failure<IReadOnlyList<AgentMessage>, string>(error)))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The reactive branch of compaction (#1755): the provider refused the
    ///     request because the context window was exceeded, so the history is
    ///     compacted FORCED — the overflow IS the trigger, so there is no
    ///     threshold to check — and the caller retries the same request from
    ///     the compacted view.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The preventive path (<see cref="BeforeTurnAsync" />) guesses
    ///         with a chars/4 heuristic, and the heuristic is systematically
    ///         low on real providers: the server tokenizer, the tool schemas,
    ///         the system prompt and the reasoning blocks are all invisible to
    ///         it. When the guess is wrong the provider refuses the request,
    ///         and this method is what turns that refusal into a compaction
    ///         instead of a dead run.
    ///     </para>
    ///     <para>
    ///         The shrink criterion is the whole contract of the retry: the
    ///         compacted view must be MEASURABLY smaller than the view the
    ///         provider refused. A compaction that folded nothing — or a
    ///         summary bigger than the head it replaced — leaves the request
    ///         exactly as overfull as it was, and the caller must not pay for
    ///         the same refusal twice. Harbor's compaction is ADDITIVE (the
    ///         summary is appended to the log and
    ///         <see cref="ICompactionView.MaterializeCompactedView" /> assembles
    ///         the slice), so "the surface shrank" is measured on the
    ///         materialized view, not on a replaced range.
    ///     </para>
    /// </remarks>
    /// <param name="session">The run's session context.</param>
    /// <param name="overfullMessages">The history the refused request was built from.</param>
    /// <param name="model">The resolved model.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>
    ///     The recovery outcome: the view a retry must be built from, and
    ///     whether the surface actually shrank.
    /// </returns>
    public async Task<OverflowRecoveryOutcome> RecoverFromOverflowAsync(
        ISessionContext session,
        IReadOnlyList<AgentMessage> overfullMessages,
        ModelInfo model,
        CancellationToken ct)
    {
        int tokensBefore = tokenTracker.EstimateTokens(overfullMessages);

        Result<IReadOnlyList<AgentMessage>, string> outcome =
            await CompactAndPublishAsync(session, overfullMessages, model, ct).ConfigureAwait(false);

        return await outcome.Match(
            compacted =>
            {
                int tokensAfter = tokenTracker.EstimateTokens(compacted);
                bool shrunk = tokensAfter < tokensBefore;

                if (shrunk)
                {
                    CompactionBehaviorLog.OverflowRecoveryCompacted(
                        logger, session.Session.Id, tokensBefore, tokensAfter);
                }
                else
                {
                    CompactionBehaviorLog.OverflowRecoveryDidNotShrink(
                        logger, session.Session.Id, tokensBefore, tokensAfter);
                }

                return Task.FromResult(new OverflowRecoveryOutcome(
                    compacted,
                    shrunk,
                    tokensBefore,
                    tokensAfter,
                    shrunk
                        ? null
                        : $"the compaction did not free the window ({tokensBefore} -> {tokensAfter} estimated tokens)"));
            },
            async error =>
            {
                // The failure is published so the run's history says WHY the
                // request was not retried — the same event the preventive path
                // publishes for a failed compaction. The preventive path also
                // engages the destructive truncation fallback here; the
                // reactive path does NOT, because the turn ends with the
                // provider's refusal either way and a fallback nobody gets to
                // use is a promise the run does not keep.
                CompactionBehaviorLog.OverflowRecoveryFailed(logger, session.Session.Id, error);
                await eventBus.PublishAsync(
                    new CompactionFailedEvent(session.Session.Id, error), CancellationToken.None)
                    .ConfigureAwait(false);
                return new OverflowRecoveryOutcome(
                    overfullMessages,
                    SurfaceShrunk: false,
                    tokensBefore,
                    tokensBefore,
                    $"the compaction failed: {error}");
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
///     The outcome of the reactive overflow recovery (#1755).
/// </summary>
/// <param name="TurnMessages">
///     The message view a retry must be built from — the compacted view when
///     the recovery shrank the surface, the overfull view when it did not.
/// </param>
/// <param name="SurfaceShrunk">
///     Whether the compacted view is measurably smaller than the view the
///     provider refused. The retry is granted only when it is: a compaction
///     that folded nothing leaves the request exactly as overfull as it was,
///     and retrying it would only pay for the same refusal twice.
/// </param>
/// <param name="TokensBefore">Estimated tokens of the overfull view.</param>
/// <param name="TokensAfter">Estimated tokens of the compacted view.</param>
/// <param name="Reason">
///     Why the retry is not allowed — the compaction failure, or the reason
///     the surface did not shrink. Null when the retry is allowed.
/// </param>
public sealed record OverflowRecoveryOutcome(
    IReadOnlyList<AgentMessage> TurnMessages,
    bool SurfaceShrunk,
    int TokensBefore,
    int TokensAfter,
    string? Reason);

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

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Context overflow recovery compacted session {SessionId}: {TokensBefore} -> {TokensAfter} estimated tokens")]
    public static partial void OverflowRecoveryCompacted(ILogger logger, string sessionId, int tokensBefore, int tokensAfter);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Context overflow recovery did not free the window for session {SessionId}: {TokensBefore} -> {TokensAfter} estimated tokens")]
    public static partial void OverflowRecoveryDidNotShrink(ILogger logger, string sessionId, int tokensBefore, int tokensAfter);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Context overflow recovery failed for session {SessionId}: {Error}")]
    public static partial void OverflowRecoveryFailed(ILogger logger, string sessionId, string error);

}
