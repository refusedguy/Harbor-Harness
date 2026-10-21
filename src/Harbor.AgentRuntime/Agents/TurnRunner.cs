using System.Text;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents.Pipeline;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     Outcome of one executed turn: the (possibly engaged) truncation fallback
///     to carry into the next turn, whether the run ends after this turn, an
///     optional terminal run failure (provider terminal error — the run
///     fails without turn/agent end events, exactly as before the extraction),
///     and which limit ended the turn when one did.
/// </summary>
/// <remarks>
///     <b>Why <c>Limit</c> exists beside the <c>bool</c>.</b> <c>EndRun</c> is a
///     bool, and a turn that ran out of budget returns <c>EndRun: true</c>
///     identically to a turn that finished the agent's work. The caller then
///     publishes one terminal fact for both, and a run cut off mid-work is
///     reported as a run that did its work. <c>Limit</c> is the reason, and it
///     is null on every non-limit exit — a bool plus a null-by-default reason is
///     cheaper than a stop-state enum and cannot drift into a second truth.
/// </remarks>
internal sealed record TurnStepResult(
    bool TruncationFallback,
    bool EndRun,
    string? RunFailure = null,
    RunLimitKind? Limit = null,
    bool Quiescent = false);

/// <summary>
///     Per-turn execution extracted from <see cref="AgentLoop" /> ([G4]):
///     provider/model resolution, prompt + request assembly, retried LLM
///     streaming, tool execution, compaction hook, background/steering drains,
///     turn-end events and the end-of-run decision. The loop itself keeps run
///     lifecycle (activity, start/end events, cancellation) and iterates turns.
/// </summary>
/// <remarks>
///     Shares the loop's logger so log categories stay identical to pre-extraction.
///     <para>
///         <b>#1527:</b> the per-delta <see cref="MessageUpdateEvent" />
///         publishes are coalesced by <see cref="UpdateCoalescer" /> into
///         batched updates, under the <see cref="UpdateFlushPolicy" /> the
///         loop hands down. The batch is flushed before every event that
///         follows it and once more before the turn's end event, so the last
///         delta of a turn is always published and the published order is the
///         order of the deltas.
///     </para>
/// </remarks>
internal sealed class TurnRunner(
    IProviderRegistry providers,
    IToolRegistry tools,
    ISystemPromptBuilder promptBuilder,
    MessageConverter messageConverter,
    IEventBus eventBus,
    ILogger logger,
    IMetrics metrics,
    ITokenTracker tokenTracker,
    IRetryPolicy retryPolicy,
    IToolDispatcher toolDispatcher,
    IMcpRegistry? mcpRegistry,
    CompactionBehavior compactionBehavior,
    SteeringDrainBehavior steering,
    BackgroundDrain backgroundDrain,
    ICompactionView? compactionView = null,
    // #1527: the flush policy of the per-delta update coalescer, and the
    // clock its interval bound is measured on. Both optional so every
    // direct-construction caller keeps compiling unchanged; null keeps the
    // shipped default (see UpdateFlushPolicy.Default).
    UpdateFlushPolicy? updateFlushPolicy = null,
    TimeProvider? clock = null,
    // #1735: the skill registry — the one place that decides which skills the
    // model may see and which it may call. Optional so every
    // direct-construction caller keeps compiling unchanged; null keeps the
    // workspace discovery and no policy.
    ISkillRegistry? skillRegistry = null,
    // #1736: the scope-aware provider resolver — the seam that lets an agent
    // run in a scope with its own LLM client. Optional so every
    // direct-construction caller keeps compiling unchanged; null keeps the
    // process-wide provider registry, which is what the product resolved
    // before scopes existed.
    IScopedProviderResolver? scopedProviders = null)
{
    // #1735: the registry the model-facing catalog comes from. Null keeps the
    // pre-registry behaviour (every discovered skill is offered), which is
    // what a directly-constructed runner gets.
    private readonly ISkillRegistry? _skillRegistry = skillRegistry;

    // C7: bounded retry budget for the LLM streaming call site only.
    // #270: 429s draw from a separate minutes-scale budget (10 attempts from a
    // 5s root, 2-minute single-sleep cap, 5-minute Retry-After clamp) instead of
    // burning the 3-attempt general budget in seconds and dying terminally.
    private static readonly RetryOptions StreamRetryOptions = new(
        MaxAttempts: 3,
        BaseDelay: TimeSpan.FromSeconds(1),
        UseJitter: true,
        MaxRateLimitAttempts: 10,
        RateLimitBaseDelay: TimeSpan.FromSeconds(5),
        MaxRetryAfter: TimeSpan.FromMinutes(5));

    /// <summary>
    ///     The boundary the durability checkpoint protects, in the words the
    ///     failure carries (#1765). The model request is the point where the
    ///     run's history becomes an answer, and an answer nobody can read
    ///     back is not worth the tokens.
    /// </summary>
    internal const string ModelRequestCheckpointReason = "before the model request";

    // #1755: the reactive overflow recovery's retry budget lives on the
    // compaction behavior (the policy owner) — the number is stated in ONE
    // place so the runtime and the tests cannot drift on it.
    internal const int MaxOverflowRetries = CompactionBehavior.MaxOverflowRetries;

    // #1100: the per-turn tool table is stable while the resolved set is.
    // FrozenToolView already memoizes ResolveTools per ruleset (repeat calls
    // return the same array instance), but BuildToolDefinitions rebuilt the
    // ToolDefinition[] every turn — N entries per turn for an identical table.
    // The built array is reused while the resolved reference is unchanged; a
    // new reference (re-freeze, ruleset swap) rebuilds once. Guarded by a
    // lock: TurnRunner is held by the singleton AgentLoop, so bare fields
    // would be the §OOP-001 shape (unsynchronised mutable state in shared code).
    private readonly object _toolTableGate = new();
    private IReadOnlyList<ToolDescriptor>? _lastResolvedTools;
    private ToolDefinition[]? _lastToolDefinitions;

    // #1612: the history-shaping seam — truncation and the compacted view run
    // through this contract, never through the CompactionPolicy statics directly.
    private readonly ICompactionView _compactionView = compactionView ?? CompactionView.Instance;

    // #1527: the coalescer's flush policy and clock. Null keeps the shipped
    // default — the policy is stated in ONE place (UpdateFlushPolicy.Default)
    // so the numbers in the docs, the tests and the runtime cannot drift.
    private readonly UpdateFlushPolicy _updateFlushPolicy = updateFlushPolicy ?? UpdateFlushPolicy.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    ///     Resolve the provider id, LLM client and concrete model for a run.
    ///     Errors are routed structurally by a flat Bind chain (parse → client →
    ///     catalog): any step failing short-circuits to the single Match exit.
    ///     The "model may be absent" case is expressed as <see cref="Maybe{T}"/>
    ///     → ToResult rather than a null-check convention.
    /// </summary>
    internal async Task<Result<(ILlmClient Client, ModelInfo Model)>> ResolveModelAsync(
        AgentDefinition agent,
        CancellationToken ct)
    {
        // ROP boundary #101: flat Bind railway with one Match exit — no nested
        // ifs, no IsFailure + .Value double-inspection.
        Result<(ILlmClient Client, IReadOnlyList<ModelInfo> Catalog)> provider =
            await ProviderId.TryCreate(agent.ProviderId)
                .Bind(id => ResolveClient(agent, id).Map(client => (id, client)))
                .Bind(async t => (await ResolveCatalog(agent, t.id, ct).ConfigureAwait(false))
                    .Map(models => (t.client, models)))
                .ConfigureAwait(false);

        return provider.Match(
            catalog => Maybe.From(FindModel(catalog.Catalog, agent.Model))
                .ToResult($"Model '{agent.Model}' not found in provider '{agent.ProviderId}'.")
                .Map(m => (Client: catalog.Client, Model: m)),
            error => Result.Failure<(ILlmClient Client, ModelInfo Model)>(error));
    }

    /// <summary>
    ///     Resolve the LLM client a run sees (#1736).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         With a scoped resolver present, the client is the one the scope
    ///         the agent runs in declares; without one, it is the client the
    ///         process-wide registry holds — the behaviour every run had before
    ///         scopes existed.
    ///     </para>
    /// </remarks>
    private Result<ILlmClient> ResolveClient(AgentDefinition agent, ProviderId providerId)
    {
        if (scopedProviders is null)
        {
            return providers.GetClient(providerId);
        }

        ServiceScope scope = scopedProviders.ScopeFor(agent.Name);
        return scopedProviders.GetClient(scope, providerId);
    }

    /// <summary>
    ///     Resolve the model catalog a run reads (#1736).
    /// </summary>
    /// <remarks>
    ///     The catalog comes from the same place the client does — the scoped
    ///     client's own list when the scope declares one, the process-wide
    ///     cached catalog otherwise. A run that read one provider's catalog and
    ///     called another provider's client would fail on a model that is
    ///     perfectly available.
    /// </remarks>
    private Task<Result<IReadOnlyList<ModelInfo>>> ResolveCatalog(
        AgentDefinition agent,
        ProviderId providerId,
        CancellationToken ct)
    {
        if (scopedProviders is null)
        {
            return providers.GetModelsCachedAsync(providerId, ct);
        }

        ServiceScope scope = scopedProviders.ScopeFor(agent.Name);
        return scopedProviders.GetModelsAsync(scope, providerId, ct);
    }

    /// <summary>
    ///     Execute one turn: turn-start event → compaction → prompt → LLM stream →
    ///     tool execution → drains → turn-end event → end-of-run decision.
    /// </summary>
    /// <returns>
    ///     The (possibly engaged) truncation fallback for the next turn and
    ///     whether the run ends after this turn.
    /// </returns>
    internal async Task<TurnStepResult> RunTurnAsync(
        ISessionContext session,
        AgentDefinition agent,
        ILlmClient client,
        ModelInfo model,
        int turn,
        bool truncationFallback,
        CancellationToken ct,
        RunBudgetTracker? budget = null)
    {
        TurnRunnerLog.TurnStart(logger, turn, agent.Name.Value, agent.Model);
        // #1103: the turn boundary is a fact, not work — it rides None so a
        // cancel landing on the publish cannot throw OCE into the run and read
        // as a provider error. Cancellation is still honored at every await
        // below; only the record of the boundary is unconditional.
        await eventBus.PublishAsync(new TurnStartEvent(turn, session.Session.Id), CancellationToken.None).ConfigureAwait(false);

        // The compacted view of the history, not the raw append-only
        // list: after a summary was produced, ShouldCompact and the
        // request both see [summary] + kept tail, so compaction does
        // not re-trigger on every subsequent turn.
        IReadOnlyList<AgentMessage> turnMessages = _compactionView.MaterializeCompactedView(session.Messages);

        // 2. Compaction check + truncation fallback — the per-turn
        // CompactionBehavior owns threshold check, summarization,
        // events/metrics and the fallback decision (§3.5).
        CompactionOutcome compactionOutcome = await compactionBehavior
            .BeforeTurnAsync(session, turnMessages, model, truncationFallback, ct, turn)
            .ConfigureAwait(false);
        turnMessages = compactionOutcome.TurnMessages;
        truncationFallback = compactionOutcome.TruncationFallback;

        // A compaction failure — including one earlier in this very
        // turn — leaves the history known-overfull: derive THIS
        // request from a strictly reduced recent tail instead.
        if (truncationFallback)
        {
            turnMessages = _compactionView.TruncateToFitStrict(turnMessages, model, tokenTracker);
            metrics.Histogram(
                "session.context.size", tokenTracker.EstimateTokens(turnMessages),
                new KeyValuePair<string, object?>("context.phase", "truncated"),
                new KeyValuePair<string, object?>("session.id", session.Session.Id));
        }

        // 3. Build system prompt
        var resolvedTools = tools.ResolveTools(agent.Name.Value, agent.Permission);
        // Cached per-directory: avoids File.Exists/Directory.GetFiles on every turn (50x regression).
        var (contextFiles, workspaceSkills) = WorkspaceContextSource.GetOrLoadCached(session.Session.Directory);
        // #1735: the model-facing catalog comes from the skill registry, which
        // merges the providers and applies the invocation policy. A skill the
        // policy keeps from the model is not in the prompt's
        // <available_skills> block at all — the model is never told about it,
        // so it cannot ask for it by name. The mode is the agent's name, the
        // axis a policy row can narrow on.
        IReadOnlyList<SkillDescriptor> skills = _skillRegistry is null
            ? workspaceSkills
            : _skillRegistry.GetSkills(session.Session.Directory, SkillCaller.Model, agent.Name.Value);
        var promptContext = new SystemPromptContext(
            agent,
            model,
            resolvedTools,
            contextFiles,
            skills,
            WorkspaceContextSource.FormatMcpInstructions(mcpRegistry?.GetInstructions()),
            session.Session.Directory);
        string systemPrompt = await promptBuilder.BuildAsync(promptContext, ct).ConfigureAwait(false);

        // 4/5. Build request — the ToolDefinition array is built once per resolved
        // set and shared across turns (#1100); a new set rebuilds once.
        var toolDefs = GetOrBuildToolDefinitions(resolvedTools);
        // Ф6/A1: the system prompt is stable across turns (tools/agent rarely change
        // mid-run and A2 memoizes rebuilds), so every request is a prefix-cache
        // candidate — flag it Ephemeral for providers that support cache_control.
        //
        // #1755: the request is built by a local function because the reactive
        // overflow recovery REBUILDS it from the compacted view — the system
        // prompt and the tool table do not change, only the history does.
        LlmRequest BuildRequest(IReadOnlyList<AgentMessage> history)
        {
            var historyMessages = messageConverter.ToLlmMessages(history);
            return LlmRequestBuilder.ForTurn(
                agent.Model,
                historyMessages,
                systemPrompt,
                toolDefs,
                model.MaxOutputTokens,
                agent.Temperature,
                agent.ReasoningEffort).Build();
        }

        var request = BuildRequest(turnMessages);

        // #1765: the durability checkpoint before the model request. The
        // request carries everything the run has committed so far: the
        // user's prompt, the previous turn's tool results, a steering
        // message drained at the last boundary. If none of that survives a
        // crash, the answer the model is about to produce is an answer to a
        // history nobody can read back, and the run's own transcript is a
        // fiction. The checkpoint is fail-closed: a failure ends the turn
        // BEFORE the adapter is dispatched.
        //
        // This is also the "before the next agent step" boundary: in Harbor
        // an agent step IS a model request, so the two are one checkpoint.
        // Everything the preceding step committed, including the tool
        // results, is covered by it.
        Result checkpoint = await session.CheckpointAsync(ModelRequestCheckpointReason, ct).ConfigureAwait(false);
        if (checkpoint.IsFailure)
        {
            // The error is the run's terminal fact: the user has to know the
            // session log is not durable, because that is why the run stopped.
            // Published with None: a terminal record must not depend on the
            // run token.
            await eventBus.PublishAsync(
                new AgentErrorEvent(checkpoint.Error), CancellationToken.None).ConfigureAwait(false);
            TurnRunnerLog.DurabilityCheckpointFailed(logger, session.Session.Id, checkpoint.Error);
            return new TurnStepResult(truncationFallback, EndRun: true, RunFailure: checkpoint.Error);
        }

        // 6. Stream LLM — wrapped in the retry policy (C7): transient provider
        //    failures (HTTP 429/5xx, network errors, timeouts) restart the
        //    whole stream attempt; fatal failures (auth/quota, caller
        //    cancellation) propagate immediately. Rate limits additionally draw
        //    from the minutes-scale budget honoring server Retry-After (#270).
        //    Each attempt rebuilds its accumulators and re-publishes
        //    MessageStart → MessageUpdate… → MessageEnd for the turn,
        //    mirroring a fresh streaming pass.
        //    #1514: the attempt's verdict is a VALUE. The provider failure
        //    travels back as a failed result carrying the transport
        //    classification, the policy reads that classification to decide
        //    whether another attempt is worth it, and the failure that ends
        //    the turn is handled below as a value — no exception crosses the
        //    attempt boundary for an expected provider failure.
        AssistantMessage? lastPartial = null;

        async Task<Result<TurnStreamResult, StreamFailure>> StreamTurnAsync(LlmRequest turnRequest)
        {
            return await retryPolicy.ExecuteValueAsync<TurnStreamResult>(
                attemptCt => ConsumeTurnStreamAsync(client, turnRequest, session, model, turn, attemptCt, reportPartial: m => lastPartial = m, budget: budget),
                StreamRetryOptions,
                (streamFailure, attempt) => TurnRunnerLog.TransientStreamFailure(logger, streamFailure.Message, attempt),
                ct).ConfigureAwait(false);
        }

        Result<TurnStreamResult, StreamFailure> outcome = await StreamTurnAsync(request).ConfigureAwait(false);

        // #1755 — the reactive branch. A provider that refuses the request
        // because the context window was exceeded is answered with a
        // compaction and a retry of the SAME request from the compacted view.
        // The retry is bounded, and it is granted only when the compaction
        // measurably shrank the surface: a compaction that folded nothing
        // leaves the request exactly as overfull as it was, and retrying it
        // would only pay for the same refusal twice.
        int overflowRetries = 0;
        string? overflowNote = null;
        while (outcome.IsFailure
               && ProviderErrors.IsContextOverflow(outcome.Error.Kind)
               && overflowRetries < MaxOverflowRetries)
        {
            // A cancelled run wins over the recovery: the compaction is an LLM
            // call of its own, and a run the user stopped must not start one.
            if (ct.IsCancellationRequested)
            {
                break;
            }

            overflowRetries++;
            OverflowRecoveryOutcome recovery = await compactionBehavior
                .RecoverFromOverflowAsync(session, turnMessages, model, ct, turn)
                .ConfigureAwait(false);

            if (!recovery.SurfaceShrunk)
            {
                overflowNote = recovery.Reason ?? "the compaction did not free the window";
                TurnRunnerLog.OverflowNotRecovered(logger, turn, overflowNote);
                break;
            }

            turnMessages = recovery.TurnMessages;
            request = BuildRequest(turnMessages);
            TurnRunnerLog.OverflowRetrying(logger, turn, overflowRetries, recovery.TokensBefore, recovery.TokensAfter);
            outcome = await StreamTurnAsync(request).ConfigureAwait(false);
        }

        if (outcome.IsFailure
            && ProviderErrors.IsContextOverflow(outcome.Error.Kind)
            && overflowNote is null)
        {
            // The retries ran out and the request still overflowed. Say so —
            // the provider's refusal alone would read as "the harness gave up
            // without trying".
            overflowNote = $"the request still overflowed after {overflowRetries} compaction attempt(s)";
        }

        if (outcome.IsFailure)
        {
            StreamFailure failure = outcome.Error;
            if (RetryPolicy.IsTransient(failure.Kind))
            {
                // #270: the transient budget (minutes-scale for 429s) is exhausted.
                // Degrade instead of dying terminally: persist whatever streamed
                // before the failure and end the run gracefully, so the session
                // stays resumable and (sub-)callers receive partial output.
                AssistantMessage degraded = lastPartial ?? AssistantMessage.Empty(session.Session.Id, model.Id);
                TurnRunnerLog.StreamBudgetExhausted(logger, failure.Message, turn, degraded.Parts.Count);
                if (degraded.Parts.Count > 0)
                {
                    await session.AppendMessageAsync(degraded, ct).ConfigureAwait(false);
                    tokenTracker.RecordAppendedMessage(degraded);
                }
                await eventBus.PublishAsync(new MessageEndEvent(degraded), ct).ConfigureAwait(false);
                // #1751: the degraded turn can carry tool calls the dispatcher never
                // reached. Close them here, or the transcript holds a tool_use with
                // no tool_result — a provider 400 on the next turn and a session
                // that cannot be resumed.
                IReadOnlyList<ToolResultMessage> degradedResults = await CloseUnansweredCallsAsync(
                    degraded, session, UnresolvedToolCall.NotStartedCode).ConfigureAwait(false);
                await eventBus.PublishAsync(
                    new TurnEndEvent(degraded, degradedResults, session.Session.Id), ct).ConfigureAwait(false);
                // #259: single collapsed-card error for the exhausted budget —
                // per-attempt publishes are suppressed above, so this is the only
                // AgentErrorEvent for the storm (never N duplicates).
                await eventBus.PublishAsync(new AgentErrorEvent(failure.Message, failure.Details), ct).ConfigureAwait(false);
                return new TurnStepResult(truncationFallback, EndRun: true);
            }

            // #1755: the context overflow is the one provider failure the turn
            // reports ITSELF — the stream consumer stays silent for it (the
            // turn may yet recover by compacting), so the error is published
            // here, on the path that actually ends the run, with the recovery
            // verdict attached.
            if (ProviderErrors.IsContextOverflow(failure.Kind))
            {
                string message = overflowNote is null
                    ? failure.Message
                    : $"{failure.Message} (context overflow recovery: {overflowNote})";
                await eventBus.PublishAsync(new AgentErrorEvent(message, failure.Details), ct).ConfigureAwait(false);
                return new TurnStepResult(truncationFallback, EndRun: true, RunFailure: message);
            }

            // Terminal provider error — same outcome as the pre-extraction
            // inline early-return: fail the run without appending the
            // partial message or publishing turn/agent end events.
            return new TurnStepResult(truncationFallback, EndRun: true, RunFailure: failure.Message);
        }

        TurnStreamResult streamed = outcome.Value;
        var partial = streamed.Partial;
        var toolCalls = streamed.ToolCalls;
        var malformedCalls = streamed.MalformedCalls;
        Usage? finalUsage = streamed.FinalUsage;
        var stopReason = streamed.StopReason;

        // #1103: the streamed output is already-produced work — it persists with
        // None so a cancel between stream and persist cannot throw OCE into the
        // run and read as a provider error. Cancellation is honored explicitly
        // right below: no tool runs for a cancelled run.
        await session.AppendMessageAsync(partial, CancellationToken.None).ConfigureAwait(false);
        // B3: feed the running token-estimate cache so ShouldCompact stays O(1).
        tokenTracker.RecordAppendedMessage(partial);
        if (finalUsage != null)
        {
            tokenTracker.RecordTurnUsage(finalUsage);
            // #653: the model that just made this call is the only thing that
            // knows what it cost, so its own rate table rides along with the
            // usage. The context folds and publishes the total; no renderer
            // prices anything (CostPricedInCoreRules).
            await session.UpdateStatsAsync(finalUsage, model.Pricing, CancellationToken.None).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            // #1103: the stop landed after the stream; the partial is persisted
            // above. Close the turn and end the run — the loop's cancel path
            // then publishes the Cancelled terminal event, and no tool executes
            // after the stop.
            logger.LogDebug("Turn {Turn} end (cancelled after stream)", turn);
            // #1751: the cancelled turn can carry tool calls the dispatcher
            // never reached — close them so the transcript stays paired.
            IReadOnlyList<ToolResultMessage> cancelledResults = await CloseUnansweredCallsAsync(
                partial, session, UnresolvedToolCall.NotStartedCode).ConfigureAwait(false);
            await eventBus.PublishAsync(
                new TurnEndEvent(partial, cancelledResults, session.Session.Id),
                CancellationToken.None).ConfigureAwait(false);
            return new TurnStepResult(truncationFallback, EndRun: true);
        }

        if (budget is not null)
        {
            // #404: fold the turn into the run's budget ledger. Reported
            // usage prices into the tariff; a turn the provider did not
            // meter falls back to the heuristic — recorded under
            // LocalEstimate, never into the billed figure, and computed
            // only when usage is absent, so a metered turn pays no
            // estimate cost on top of the usage it already reported.
            int estimate = finalUsage is null ? tokenTracker.EstimateMessage(partial) : 0;
            budget.CompleteRequest(finalUsage, model.Pricing, estimate);
        }

        // 7. Turn-end decision. A run ends when the turn produced no
        // tool activity, or the stream was aborted mid-flight (never
        // execute tools for a cancelled run).
        TurnRunnerLog.TurnStats(logger, turn, toolCalls.Count, malformedCalls.Count, stopReason);
        if ((toolCalls.Count == 0 && malformedCalls.Count == 0) || stopReason == StopReason.Aborted)
        {
            TurnRunnerLog.TurnEndNoToolCalls(logger, turn);
            // #1751: an aborted turn can carry tool calls the dispatcher never
            // reached — close them so the transcript stays paired.
            IReadOnlyList<ToolResultMessage> abortedResults = await CloseUnansweredCallsAsync(
                partial, session, UnresolvedToolCall.NotStartedCode).ConfigureAwait(false);
            await eventBus.PublishAsync(
                new TurnEndEvent(partial, abortedResults, session.Session.Id), ct).ConfigureAwait(false);
            // #404: a capped text-only turn is a limit stop, not a finish
            // (the #403 shape on the budget axis). Cancel still wins: an
            // aborted stream ends as a cancel, never as a cap.
            if (budget is not null && stopReason != StopReason.Aborted)
            {
                // Absence is a Maybe (#1557): "no cap tripped" is a state of
                // the ledger, not a missing value the caller has to guess at.
                Maybe<RunLimitKind> earlyHit = budget.CheckCap();
                if (earlyHit.HasValue)
                    return new TurnStepResult(truncationFallback, EndRun: true, Limit: earlyHit.Value);
            }

            // #1761: QUIESCENCE. The turn produced no tool activity, so the
            // agent has nothing left to do on its own — the only moment at
            // which "the agent went quiet" is a fact rather than a guess.
            // The caller (the loop) uses this to decide whether a goal wants
            // another round; an aborted stream is not quiescence, it is a
            // cancel, and it never opens a round.
            bool quiescent = toolCalls.Count == 0
                             && malformedCalls.Count == 0
                             && stopReason != StopReason.Aborted;
            return new TurnStepResult(truncationFallback, EndRun: true, Quiescent: quiescent);
        }

        // Some providers report a terminal finish_reason (stop / length)
        // even when tool calls are present. Dropping them silently loses
        // model intent — execute them through the normal path, persist
        // the results, publish the turn end, then finish the run.
        bool runEndsAfterExecution = stopReason is StopReason.Stop or StopReason.Length;

        // 8. Execute tool calls (+ synthesize error results for malformed ones)
        var toolResults = await ExecuteTurnToolCallsAsync(
            toolCalls, malformedCalls, session, partial, agent, ct).ConfigureAwait(false);

        // Persist the tool results so the next turn can feed them back
        // to the model (OpenAI requires a `tool` role message after a
        // tool_call, otherwise the model loops calling the same tool).
        await session.AppendMessageAsync(toolResults, ct).ConfigureAwait(false);
        // B3: tool results are pure appends — extend the running estimate.
        tokenTracker.RecordAppendedMessage(toolResults);

        // Background-task ping ("boss, I'm done"): finished detached
        // runs append as tool results so the NEXT turn picks them up.
        await backgroundDrain.DrainAsync(session, ct).ConfigureAwait(false);

        // Ф2/B2: mid-run steering injection INSIDE the turn. Drained
        // right AFTER the tool results are persisted (never between
        // the assistant tool_calls and their results — providers
        // require that adjacency) so the NEXT LLM request of THIS run
        // already carries the steering, not just the next turn.
        await steering.DrainAsync(session, ct).ConfigureAwait(false);

        TurnRunnerLog.TurnEndWithResults(logger, turn);
        await eventBus.PublishAsync(
            new TurnEndEvent(partial, new[] { toolResults }, session.Session.Id), ct).ConfigureAwait(false);

        // 9. Boundary steering drain — kept for runs that reach max
        // steps or a terminal stop reason right after execution; on
        // the normal path it is a no-op (B2 drained above).
        await steering.DrainAsync(session, ct).ConfigureAwait(false);

        // 10. Budget cap — the spend ceiling at the same safe boundary as
        // the step ceiling below. Usage arrives AFTER the request that spent
        // it, so the tripping turn's tools have already run and been
        // persisted above: that is the modeled overshoot (LateUsage), not a
        // leak. A spend ceiling outranks a step ceiling — money is the more
        // specific bound — so this check comes first.
        if (budget is not null)
        {
            // Absence is a Maybe (#1557): the cap names the limit that
            // tripped, and "none tripped" is a state, not a null to
            // pattern-match away.
            Maybe<RunLimitKind> budgetHit = budget.CheckCap();
            if (budgetHit.HasValue)
            {
                TurnRunnerLog.BudgetCapHit(logger, budgetHit.Value);
                return new TurnStepResult(truncationFallback, EndRun: true, Limit: budgetHit.Value);
            }
        }

        // 11. Max steps — also honoured after a terminal stop reason.
        if (MaxStepsBehavior.IsExhausted(turn, agent))
        {
            TurnRunnerLog.MaxStepsReached(logger, agent.MaxSteps);
            // #403: EndRun alone cannot say WHY. Without this the loop published
            // the same terminal event as a run that finished its work, and
            // `SessionStatus.Done` was the user's answer for a run that was cut
            // off with steps left to take. The limit is the fact; the bool is
            // only the shape the loop iterates on.
            return new TurnStepResult(truncationFallback, EndRun: true, Limit: RunLimitKind.MaxSteps);
        }

        if (runEndsAfterExecution)
        {
            TurnRunnerLog.TerminalStopWithTools(logger, turn, stopReason);
            return new TurnStepResult(truncationFallback, EndRun: true);
        }

        return new TurnStepResult(truncationFallback, EndRun: false);
    }

    /// <summary>
    ///     One complete streaming attempt for a turn: publish
    ///     <see cref="MessageStartEvent" />, consume the provider stream while
    ///     coalescing deltas, synthesize placeholders for malformed tool calls,
    ///     and publish <see cref="MessageEndEvent" />.
    /// </summary>
    /// <remarks>
    ///     Invoked through <see cref="IRetryPolicy" /> (C7). A retry re-runs the
    ///     whole attempt from scratch — fresh accumulators, fresh events — so no
    ///     state from a failed attempt can leak into the retried one. Cancellation
    ///     by the caller is converted into a graceful <see cref="StopReason.Aborted" />
    ///     outcome, exactly as before the extraction.
    ///     On a terminal stream error the flushed-so-far partial (stamped
    ///     <see cref="StopReason.Error" />) is handed to <paramref name="reportPartial" />
    ///     before the failure travels back, so budget exhaustion (#270) can degrade
    ///     with the last attempt's output instead of losing it.
    ///     #1514: the attempt's verdict is a VALUE. A provider failure comes back
    ///     as a failed result carrying the transport classification — the retry
    ///     decision reads that value instead of catching an exception.
    /// </remarks>
    private async Task<Result<TurnStreamResult, StreamFailure>> ConsumeTurnStreamAsync(
        ILlmClient client,
        LlmRequest request,
        ISessionContext session,
        ModelInfo model,
        int turn,
        CancellationToken ct,
        Action<AssistantMessage>? reportPartial = null,
        RunBudgetTracker? budget = null)
    {
        var partial = AssistantMessage.Empty(session.Session.Id, model.Id);
        TurnRunnerLog.MessageStart(logger, turn);
        await eventBus.PublishAsync(new MessageStartEvent(partial), ct).ConfigureAwait(false);

        // Pre-size to typical tool-call count to avoid List resizes.
        var toolCalls = new List<ToolCallPart>(capacity: 4);
        // Tool calls whose streamed args JSON failed to parse (C4) —
        // reported by the coalescer, converted into error tool results
        // below instead of being executed with fabricated empty args.
        var malformedCalls = new List<MalformedToolCall>();
        Usage? finalUsage = null;
        var stopReason = StopReason.Stop;

        using var coalescer = new StreamingCoalescer();

        // #1527: the per-delta MessageUpdateEvent publishes are coalesced into
        // batched updates. The coalescer owns the pending batch; the turn
        // runner flushes it at every boundary (a change of delta kind, a
        // tool-call event, the step finish, a stream error) and once more
        // before MessageEndEvent — the tail guarantee: the last delta of a
        // turn is ALWAYS published, never dropped.
        using var updates = new UpdateCoalescer(eventBus, _updateFlushPolicy, _clock);

        // #1104: the output-size break below stops READING while the
        // provider pump keeps WRITING. The linked source lets this consumer
        // kill its producer on early break; everything past the loop keeps
        // the caller's ct (MessageEnd must publish even on a capped break).
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await foreach (var evt in client.StreamAsync(request, streamCts.Token).ConfigureAwait(false))
            {
                // LogTrace is the most frequent log call in the hot path (one per
                // stream event = potentially thousands per turn). Guarding it with
                // IsEnabled avoids the params object?[] array allocation that
                // LogTrace incurs even when trace logging is off.
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    TurnRunnerLog.StreamEvent(logger, evt.GetType().Name);
                }
                switch (evt)
                {
                    case TextDeltaEvent td:
                        // Flush any pending thinking before starting/continuing a text run.
                        partial = FlushThinking(coalescer, partial);
                        coalescer.AppendTextDelta(td.Delta);
                        // #1527: the delta is BUFFERED, not published — the
                        // coalescer decides when the batch goes out.
                        await updates.AppendTextAsync(td, partial, ct).ConfigureAwait(false);
                        budget?.RecordOutputBytes(Encoding.UTF8.GetByteCount(td.Delta));
                        break;

                    case ThinkingDeltaEvent thd:
                        // Flush any pending text before starting/continuing a thinking run.
                        partial = FlushText(coalescer, partial);
                        coalescer.AppendThinkingDelta(thd.Delta);
                        // #1527: buffered, like the text delta.
                        await updates.AppendThinkingAsync(thd, partial, ct).ConfigureAwait(false);
                        budget?.RecordOutputBytes(Encoding.UTF8.GetByteCount(thd.Delta));
                        break;

                    case ToolCallStartEvent tcs:
                        // #1527: the pending batch goes out BEFORE the
                        // tool-call event, so the published order is the order
                        // of the deltas.
                        await updates.FlushAsync(ct).ConfigureAwait(false);
                        partial = FlushAll(coalescer, partial);
                        coalescer.StartToolCall(tcs.Id, tcs.ToolName);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        break;

                    case ToolCallDeltaEvent tcd:
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            TurnRunnerLog.ToolCallDelta(logger, tcd.Id, tcd.ArgsDelta);
                        }
                        // #1527: a tool-call fragment is never merged (its id
                        // could belong to another call), but the pending text
                        // batch still goes out first.
                        await updates.FlushAsync(ct).ConfigureAwait(false);
                        coalescer.AppendToolCallDelta(tcd.Id, tcd.ArgsDelta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        budget?.RecordOutputBytes(Encoding.UTF8.GetByteCount(tcd.ArgsDelta));
                        break;

                    case StepFinishEvent sf:
                    {
                        StepOutcome outcome = await FinalizeStepAsync(sf, coalescer, updates, partial, malformedCalls, ct).ConfigureAwait(false);
                        partial = outcome.Partial;
                        toolCalls.AddRange(outcome.MaterializedCalls);
                        finalUsage = outcome.FinalUsage;
                        stopReason = outcome.StopReason;
                        break;
                    }

                    case ErrorEvent err:
                        // #1527: the pending batch goes out BEFORE the error
                        // path, so the text streamed so far is not lost with
                        // the turn.
                        await updates.FlushAsync(ct).ConfigureAwait(false);
                        // Flush buffered deltas into the partial and report it
                        // before propagating (#270): on budget exhaustion the
                        // turn degrades with this output instead of losing it.
                        // Then discard any per-tool-call pooled StringBuilders
                        // (same as the previous inline early-return out of RunAsync).
                        partial = FlushAll(coalescer, partial);
                        coalescer.DiscardPendingToolCalls();
                        reportPartial?.Invoke(partial.WithFinish(StopReason.Error, finalUsage ?? new Usage(0, 0)));
                        // #259: transient failures are retried — publishing here
                        // would emit one AgentError per attempt (the same 429
                        // blob N times). Only fatal errors publish immediately,
                        // transient ones publish once when the budget is
                        // exhausted (see the caller), zero on recovery.
                        // #1755: a context overflow is silent here for the same
                        // reason — the turn may yet recover by compacting and
                        // retrying, and an error published for a request that
                        // then succeeds reads as a failure the run did not
                        // have. The turn publishes it when the recovery does
                        // not happen.
                        if (!ProviderErrors.IsTransient(err.Kind)
                            && !ProviderErrors.IsContextOverflow(err.Kind))
                        {
                            await eventBus.PublishAsync(new AgentErrorEvent(err.Message, err.Exception), ct).ConfigureAwait(false);
                        }

                        // #1514: the provider failure is a VALUE. The retry
                        // decision reads its classification instead of catching
                        // an exception, and the caller receives the same message
                        // and details the exception used to carry.
                        return Result.Failure<TurnStreamResult, StreamFailure>(StreamFailure.From(err));
                }

                // #404: the output-size cap is enforced on the delta path —
                // stop pulling the stream instead of buffering the rest of
                // the response. Token/spend fold in after the stream, so only
                // the output axis can newly trip here; the post-stream check
                // reports the limit kind.
                // Absence is a Maybe (#1557) — a struct, so the "no tracker"
                // case is a null Nullable and the "no cap" case is None.
                Maybe<RunLimitKind> deltaHit = budget?.CheckCap() ?? Maybe<RunLimitKind>.None;
                if (deltaHit.HasValue && deltaHit.Value == RunLimitKind.MaxOutputBytes)
                {
                    // #1104: producer dies with the consumer — without this
                    // the fire-and-forget pump keeps pouring the whole
                    // response into the channel after nobody reads it.
                    streamCts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Flush pending buffers so nothing is lost, then discard pooled
            // StringBuilders — cancellation mid-stream would otherwise leak them.
            partial = FlushAll(coalescer, partial);
            coalescer.DiscardPendingToolCalls();
            partial = partial.WithFinish(StopReason.Aborted, finalUsage ?? new Usage(0, 0));
            // Align the loop state with the aborted finish: previously the
            // stale stopReason/toolCalls from an earlier StepFinish could
            // cause tool execution AFTER cancellation.
            stopReason = StopReason.Aborted;
            toolCalls.Clear();
            malformedCalls.Clear();
        }
        finally
        {
            // #1527 — THE TAIL GUARANTEE. Whatever the flush policy decided,
            // the last delta of the turn is published here, BEFORE the turn's
            // end event: a buffered delta is never dropped, and the end event
            // never overtakes the text it closes. The abort path above lands
            // here too, and so does a stream that threw — a delta the
            // provider already sent is published whether or not the turn
            // finished.
            await updates.FlushAsync(ct).ConfigureAwait(false);
        }

        partial = AppendMalformedPlaceholders(partial, malformedCalls);

        TurnRunnerLog.MessageEnd(logger, turn, stopReason);
        await eventBus.PublishAsync(new MessageEndEvent(partial), ct).ConfigureAwait(false);

        return Result.Success<TurnStreamResult, StreamFailure>(
            new TurnStreamResult(partial, toolCalls, malformedCalls, finalUsage, stopReason));
    }

    /// <summary>
    ///     Flush a pending text run into the message (ROP-C flush choreography).
    ///     Forwarder over <see cref="MessageFlush" /> (#1101, slice 1: verbatim move).
    /// </summary>
    private static AssistantMessage FlushText(StreamingCoalescer coalescer, AssistantMessage partial) =>
        MessageFlush.FlushText(coalescer, partial);

    /// <summary>
    ///     Flush a pending thinking run into the message.
    ///     Forwarder over <see cref="MessageFlush" /> (#1101, slice 1: verbatim move).
    /// </summary>
    private static AssistantMessage FlushThinking(StreamingCoalescer coalescer, AssistantMessage partial) =>
        MessageFlush.FlushThinking(coalescer, partial);

    /// <summary>
    ///     Flush both buffer kinds in wire order (text first, then thinking) —
    ///     the sequence every non-delta arm and the abort path must perform
    ///     (ROP-C П.3: five copies of this dance collapse to one helper).
    ///     Forwarder over <see cref="MessageFlush" /> (#1101, slice 1: verbatim move).
    /// </summary>
    private static AssistantMessage FlushAll(StreamingCoalescer coalescer, AssistantMessage partial) =>
        MessageFlush.FlushAll(coalescer, partial);

    /// <summary>
    ///     Publish one MessageUpdateEvent for a stream event that is NOT a
    ///     mergeable delta (#1527): a tool-call start, a tool-call fragment,
    ///     the step finish. The pending delta batch is flushed before each of
    ///     them, so the published order stays the order of the stream.
    /// </summary>
    private Task PublishUpdateAsync(LlmEvent evt, AssistantMessage partial, CancellationToken ct) =>
        eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct);

    /// <summary>
    ///     Finalize one provider step: flush buffers, materialize accumulated
    ///     tool-call fragments (un-parseable ones are reported via
    ///     <paramref name="malformedCalls" />), stamp usage + stop reason, and
    ///     forward the finish to the bus so status bars can tally tokens.
    /// </summary>
    private async Task<StepOutcome> FinalizeStepAsync(
        StepFinishEvent sf,
        StreamingCoalescer coalescer,
        UpdateCoalescer updates,
        AssistantMessage partial,
        List<MalformedToolCall> malformedCalls,
        CancellationToken ct)
    {
        // #1527: the pending batch goes out BEFORE the step finish — the
        // finish carries the usage, the batch carries the text, and the text
        // must not arrive after the event that closes it.
        await updates.FlushAsync(ct).ConfigureAwait(false);

        partial = FlushAll(coalescer, partial);

        var materializedCalls = coalescer.MaterializeToolCalls(malformedCalls);
        for (int i = 0; i < materializedCalls.Count; i++)
        {
            partial = partial.AppendToolCall(materializedCalls[i]);
        }

        var stopReason = StopReasonJsonConverter.Parse(sf.FinishReason);
        partial = partial.WithFinish(stopReason, sf.Usage ?? new Usage(0, 0));
        await PublishUpdateAsync(sf, partial, ct).ConfigureAwait(false);
        return new StepOutcome(partial, materializedCalls, sf.Usage, stopReason);
    }

    /// <summary>Per-step finalize result handed back to the stream loop.</summary>
    private sealed record StepOutcome(
        AssistantMessage Partial,
        List<ToolCallPart> MaterializedCalls,
        Usage? FinalUsage,
        StopReason StopReason);

    /// <summary>
    ///     Surface malformed tool calls (C4): keep the assistant message's
    ///     wire shape consistent by appending a placeholder part per call —
    ///     every tool_call must be answered by a tool_result — while the
    ///     error result built by the turn tells the model its args were
    ///     un-parseable.
    /// </summary>
    private AssistantMessage AppendMalformedPlaceholders(AssistantMessage partial, List<MalformedToolCall> malformedCalls)
    {
        for (int i = 0; i < malformedCalls.Count; i++)
        {
            var malformed = malformedCalls[i];
            TurnRunnerLog.MalformedArgs(logger, malformed.Id, malformed.ToolName, malformed.RawArgsTail);
            partial = partial.AppendToolCall(new ToolCallPart(malformed.Id, malformed.ToolName, EmptyJsonArgs()));
        }

        return partial;
    }

    /// <summary>
    ///     Terminal outcome of one streaming attempt (see
    ///     <see cref="ConsumeTurnStreamAsync" />).
    /// </summary>
    private sealed record TurnStreamResult(
        AssistantMessage Partial,
        List<ToolCallPart> ToolCalls,
        List<MalformedToolCall> MalformedCalls,
        Usage? FinalUsage,
        StopReason StopReason);

    /// <summary>
    ///     Execute the turn's tool calls and synthesize error results for
    ///     malformed ones. Valid calls go through <see cref="ToolDispatcher" />
    ///     (permission gating + events); malformed calls never reach a tool —
    ///     each gets an <c>IsError=true</c> result carrying the raw args tail so
    ///     the model can retry with well-formed JSON next turn.
    /// </summary>
    /// <remarks>
    ///     #401 B2 (Abandoned visibility): calls that outlived the accepted
    ///     stop arrive here already marked Abandoned by the dispatcher and are
    ///     persisted like any other result — that is what makes the abandonment
    ///     observable from <c>RunOutcome.Reconstruct</c> per-call state instead
    ///     of a silent success. This method adds no second marking; it counts
    ///     them for the log so the boundary is visible at runtime too.
    /// </remarks>
    private async Task<ToolResultMessage> ExecuteTurnToolCallsAsync(
        List<ToolCallPart> toolCalls,
        List<MalformedToolCall> malformedCalls,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct)
    {
        var results = new List<ToolResultEntry>(toolCalls.Count + malformedCalls.Count);

        if (toolCalls.Count > 0)
        {
            var executed = await toolDispatcher.ExecuteAsync(
                toolCalls, session, partial, agent, ct,
                agent.ToolTimeoutSeconds is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null).ConfigureAwait(false);
            results.AddRange(executed.Results);
        }

        for (int i = 0; i < malformedCalls.Count; i++)
        {
            var malformed = malformedCalls[i];
            results.Add(new ToolResultEntry(
                malformed.Id,
                malformed.ToolName,
                $"Malformed JSON arguments for tool '{malformed.ToolName}' — tool was NOT executed. Raw arguments tail: {malformed.RawArgsTail}",
                true));
        }

        // #401 B2: make the Accepted boundary visible at runtime — Abandoned
        // entries persist above like any other result; the count lands in the
        // log next to the dispatcher's own accepted-stop record.
        int abandoned = 0;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Output.Contains(
                    ToolCallLink.AbandonedOutputMarker, StringComparison.Ordinal))
            {
                abandoned++;
            }
        }

        if (abandoned > 0)
        {
            TurnRunnerLog.TurnCarriedAbandoned(logger, abandoned);
        }

        return new ToolResultMessage(
            Guid.NewGuid().ToString("N"),
            session.Session.Id,
            DateTimeOffset.UtcNow,
            results);
    }

    /// <summary>
    ///     Frozen empty-JSON-object element shared by every malformed-call
    ///     placeholder. Same convention as <see cref="StreamingCoalescer" />'s
    ///     zero-delta empty-args element: the backing <see cref="JsonDocument" />
    ///     is never disposed (process-lifetime state), so the element stays
    ///     valid for all consumers; concurrent reads are safe because parsed
    ///     JSON is immutable. Avoids a per-call <c>Parse("{}") + Clone()</c>.
    /// </summary>
    private static readonly JsonElement EmptyMalformedArgsElement = JsonDocument.Parse("{}").RootElement;

    /// <summary>
    ///     Placeholder args for a malformed tool call's assistant-side part.
    ///     The real arguments were un-parseable; the error tool_result carries
    ///     the diagnostics, while this keeps the tool_call ↔ tool_result
    ///     pairing providers require.
    /// </summary>
    private static JsonElement EmptyJsonArgs() => EmptyMalformedArgsElement;

    /// <summary>
    ///     Close the tool calls an assistant message opened and the turn never
    ///     answered (#1751).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <c>tool_use</c> without a matching <c>tool_result</c> is a
    ///         provider error: Anthropic wants every tool_use answered in the
    ///         next message, OpenAI wants a <c>tool</c> role message with the
    ///         same id. The transcript would be invalid and the session
    ///         unresumable.
    ///     </para>
    ///     <para>
    ///         The turn runner is the only place that KNOWS the call never
    ///         started — the dispatcher was never reached — so the closure
    ///         carries <see cref="UnresolvedToolCall.NotStartedCode" />: the
    ///         call had no effect and the model may retry it. (The repairer in
    ///         the message converter cannot know this and says
    ///         <see cref="UnresolvedToolCall.OutcomeUnknownCode" /> instead.)
    ///     </para>
    ///     <para>
    ///         The closure is persisted, so the transcript pairs every tool_use
    ///         with a tool_result, and published as one
    ///         <see cref="ToolExecutionEndEvent" /> per call — the event the
    ///         dispatcher publishes for a call that failed, so a renderer shows
    ///         WHICH call is unresolved instead of leaving a card spinning.
    ///     </para>
    /// </remarks>
    /// <param name="assistant">The assistant message whose calls went unanswered.</param>
    /// <param name="session">The session the closure belongs to.</param>
    /// <param name="code">One of the <see cref="UnresolvedToolCall" /> codes.</param>
    /// <returns>
    ///     The persisted closure — one tool-result message holding an error
    ///     entry per unanswered call — or an empty list when the message asked
    ///     for no tool at all (nothing to close).
    /// </returns>
    private async Task<IReadOnlyList<ToolResultMessage>> CloseUnansweredCallsAsync(
        AssistantMessage assistant,
        ISessionContext session,
        string code)
    {
        ToolResultMessage? closure = UnresolvedToolCallRepair.Close(assistant, code);
        if (closure is null)
        {
            return Array.Empty<ToolResultMessage>();
        }

        // The closure is already-produced work: it rides None so a cancel
        // racing the persist cannot throw OCE into the run and lose the
        // pairing the provider needs.
        await session.AppendMessageAsync(closure, CancellationToken.None).ConfigureAwait(false);
        tokenTracker.RecordAppendedMessage(closure);

        // One end event per call — the unresolved call is named, not hidden.
        // The end of a call is a terminal FACT: it rides None (the same rule
        // the dispatcher's terminal publishes follow), so a cancel racing the
        // publish cannot swallow the fact that a call never got its result.
        for (int i = 0; i < closure.Results.Count; i++)
        {
            ToolResultEntry entry = closure.Results[i];
            await eventBus.PublishAsync(
                new ToolExecutionEndEvent(entry.ToolCallId, ToolResult.Error(entry.Output), true),
                CancellationToken.None).ConfigureAwait(false);
        }

        return new[] { closure };
    }

    /// <summary>
    ///     Linear scan for the requested model — avoids LINQ FirstOrDefault delegate allocation.
    /// </summary>
    private static ModelInfo? FindModel(IReadOnlyList<ModelInfo> models, string modelId)
    {
        for (int i = 0; i < models.Count; i++)
        {
            if (models[i].Id == modelId)
            {
                return models[i];
            }
        }
        return null;
    }

    /// <summary>
    ///     The turn's <see cref="ToolDefinition" /> table, rebuilt only when the
    ///     resolved set moves (#1100). Repeat turns with the same resolved
    ///     reference (the frozen memoised array for an unchanged ruleset) share
    ///     one built array instead of paying N entries per turn.
    /// </summary>
    /// <remarks>
    ///     Check-then-build runs outside the gate like
    ///     <c>FrozenToolView.Resolve</c>: holding a lock across N entries would
    ///     serialise concurrent first-time builds. A lost race only rebuilds —
    ///     the stored pair is always written together under the gate, so it can
    ///     never mix generations. The returned array is shared across turns and
    ///     must be treated as read-only (providers serialise it; nothing mutates it).
    /// </remarks>
    private ToolDefinition[] GetOrBuildToolDefinitions(IReadOnlyList<ToolDescriptor> resolvedTools)
    {
        lock (_toolTableGate)
        {
            if (_lastToolDefinitions is not null && ReferenceEquals(_lastResolvedTools, resolvedTools))
            {
                return _lastToolDefinitions;
            }
        }

        ToolDefinition[] built = ToolTableBuilder.BuildToolDefinitions(resolvedTools);

        lock (_toolTableGate)
        {
            _lastResolvedTools = resolvedTools;
            _lastToolDefinitions = built;
        }

        return built;
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="TurnRunner" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class TurnRunnerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Turn {Turn} start: agent={Agent} model={Model}")]
    public static partial void TurnStart(ILogger logger, int turn, string agent, string model);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Transient LLM stream failure on attempt {Attempt}; retrying: {FailureMessage}")]
    public static partial void TransientStreamFailure(ILogger logger, string failureMessage, int attempt);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "LLM stream budget exhausted on turn {Turn}; degrading with partial output ({Parts} parts): {FailureMessage}")]
    public static partial void StreamBudgetExhausted(ILogger logger, string failureMessage, int turn, int parts);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Turn {Turn}: toolCalls={ToolCalls} malformed={Malformed} stopReason={StopReason}")]
    public static partial void TurnStats(ILogger logger, int turn, int toolCalls, int malformed, StopReason stopReason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Turn {Turn} end (no tool calls)")]
    public static partial void TurnEndNoToolCalls(ILogger logger, int turn);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Turn {Turn} end (with tool results)")]
    public static partial void TurnEndWithResults(ILogger logger, int turn);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Agent hit budget cap ({Limit})")]
    public static partial void BudgetCapHit(ILogger logger, RunLimitKind limit);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Agent reached max steps ({MaxSteps})")]
    public static partial void MaxStepsReached(ILogger logger, int maxSteps);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Turn {Turn}: terminal stop reason ({StopReason}) with executed tool calls — ending run")]
    public static partial void TerminalStopWithTools(ILogger logger, int turn, StopReason stopReason);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Message start: turn={Turn}")]
    public static partial void MessageStart(ILogger logger, int turn);

    [LoggerMessage(EventId = 11, Level = LogLevel.Trace, Message = "Stream event: {EventType}")]
    public static partial void StreamEvent(ILogger logger, string eventType);

    [LoggerMessage(EventId = 12, Level = LogLevel.Trace, Message = "ToolCallDelta id={Id} argsDelta={Args}")]
    public static partial void ToolCallDelta(ILogger logger, string id, string args);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug, Message = "Message end: turn={Turn} stopReason={StopReason}")]
    public static partial void MessageEnd(ILogger logger, int turn, StopReason stopReason);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Malformed JSON arguments for tool call {CallId} ({ToolName}); raw tail: {ArgsTail}")]
    public static partial void MalformedArgs(ILogger logger, string callId, string toolName, string argsTail);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug, Message = "Turn carried {Abandoned} abandoned tool call(s) after the accepted stop")]
    public static partial void TurnCarriedAbandoned(ILogger logger, int abandoned);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Turn {Turn} hit a context overflow; compacted {TokensBefore} -> {TokensAfter} estimated tokens and is retrying the request (attempt {Attempt})")]
    public static partial void OverflowRetrying(ILogger logger, int turn, int attempt, int tokensBefore, int tokensAfter);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning, Message = "Turn {Turn} hit a context overflow and the request was not retried: {Reason}")]
    public static partial void OverflowNotRecovered(ILogger logger, int turn, string reason);

    // #1765: the id continues after #1755's 16 and 17, so the ids dev already
    // shipped for the overflow recovery keep their numbers.
    [LoggerMessage(EventId = 18, Level = LogLevel.Error, Message = "Durability checkpoint failed for session {SessionId}; the model request was not dispatched: {Error}")]
    public static partial void DurabilityCheckpointFailed(ILogger logger, string sessionId, string error);

}
