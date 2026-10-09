using System.Text;
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
    RunLimitKind? Limit = null);

/// <summary>
///     Per-turn execution extracted from <see cref="AgentLoop" /> ([G4]):
///     provider/model resolution, prompt + request assembly, retried LLM
///     streaming, tool execution, compaction hook, background/steering drains,
///     turn-end events and the end-of-run decision. The loop itself keeps run
///     lifecycle (activity, start/end events, cancellation) and iterates turns.
/// </summary>
/// <remarks>
///     Shares the loop's logger so log categories stay identical to pre-extraction.
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
    BackgroundDrain backgroundDrain)
{
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
                .Bind(id => providers.GetClient(id).Map(client => (id, client)))
                .Bind(async t => (await providers.GetModelsCachedAsync(t.id, ct).ConfigureAwait(false))
                    .Map(models => (t.client, models)))
                .ConfigureAwait(false);

        return provider.Match(
            catalog => Maybe.From(FindModel(catalog.Catalog, agent.Model))
                .ToResult($"Model '{agent.Model}' not found in provider '{agent.ProviderId}'.")
                .Map(m => (Client: catalog.Client, Model: m)),
            error => Result.Failure<(ILlmClient Client, ModelInfo Model)>(error));
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
        logger.LogDebug("Turn {Turn} start: agent={Agent} model={Model}", turn, agent.Name.Value, agent.Model);
        await eventBus.PublishAsync(new TurnStartEvent(turn, session.Session.Id), ct).ConfigureAwait(false);

        // The compacted view of the history, not the raw append-only
        // list: after a summary was produced, ShouldCompact and the
        // request both see [summary] + kept tail, so compaction does
        // not re-trigger on every subsequent turn.
        IReadOnlyList<AgentMessage> turnMessages = CompactionPolicy.MaterializeCompactedView(session.Messages);

        // 2. Compaction check + truncation fallback — the per-turn
        // CompactionBehavior owns threshold check, summarization,
        // events/metrics and the fallback decision (§3.5).
        CompactionOutcome compactionOutcome = await compactionBehavior
            .BeforeTurnAsync(session, turnMessages, model, truncationFallback, ct)
            .ConfigureAwait(false);
        turnMessages = compactionOutcome.TurnMessages;
        truncationFallback = compactionOutcome.TruncationFallback;

        // A compaction failure — including one earlier in this very
        // turn — leaves the history known-overfull: derive THIS
        // request from a strictly reduced recent tail instead.
        if (truncationFallback)
        {
            turnMessages = CompactionPolicy.TruncateToFitStrict(turnMessages, model, tokenTracker);
            metrics.Histogram(
                "session.context.size", tokenTracker.EstimateTokens(turnMessages),
                new KeyValuePair<string, object?>("context.phase", "truncated"),
                new KeyValuePair<string, object?>("session.id", session.Session.Id));
        }

        // 3. Build system prompt
        var resolvedTools = tools.ResolveTools(agent.Name.Value, agent.Permission);
        // Cached per-directory: avoids File.Exists/Directory.GetFiles on every turn (50x regression).
        var (contextFiles, skills) = WorkspaceContextSource.GetOrLoadCached(session.Session.Directory);
        var promptContext = new SystemPromptContext(
            agent,
            model,
            resolvedTools,
            contextFiles,
            skills,
            WorkspaceContextSource.FormatMcpInstructions(mcpRegistry?.GetInstructions()),
            session.Session.Directory);
        string systemPrompt = await promptBuilder.BuildAsync(promptContext, ct).ConfigureAwait(false);

        // 4. Convert messages — the truncated view after a compaction
        // failure, the full history otherwise.
        var llmMessages = messageConverter.ToLlmMessages(turnMessages);

        // 5. Build request — size the ToolDefinition array directly instead of LINQ Select().ToList().
        var toolDefs = ToolTableBuilder.BuildToolDefinitions(resolvedTools);
        // Ф6/A1: the system prompt is stable across turns (tools/agent rarely change
        // mid-run and A2 memoizes rebuilds), so every request is a prefix-cache
        // candidate — flag it Ephemeral for providers that support cache_control.
        var request = new LlmRequest(
            agent.Model,
            llmMessages,
            systemPrompt,
            toolDefs,
            MaxOutputTokens: model.MaxOutputTokens,
            Temperature: agent.Temperature,
            ReasoningEffort: agent.ReasoningEffort,
            CacheStrategy: systemPrompt.Length > 0 ? CacheStrategy.Ephemeral : CacheStrategy.None);

        // 6. Stream LLM — wrapped in the retry policy (C7): transient provider
        //    failures (HTTP 429/5xx, network errors, timeouts) restart the
        //    whole stream attempt; fatal failures (auth/quota, caller
        //    cancellation) propagate immediately. Rate limits additionally draw
        //    from the minutes-scale budget honoring server Retry-After (#270).
        //    Each attempt rebuilds its accumulators and re-publishes
        //    MessageStart → MessageUpdate… → MessageEnd for the turn,
        //    mirroring a fresh streaming pass.
        TurnStreamResult streamed;
        AssistantMessage? lastPartial = null;
        try
        {
            streamed = await retryPolicy.ExecuteAsync(
                attemptCt => ConsumeTurnStreamAsync(client, request, session, model, turn, attemptCt, reportPartial: m => lastPartial = m, budget: budget),
                StreamRetryOptions,
                (ex, attempt) => logger.LogWarning(
                    ex, "Transient LLM stream failure on attempt {Attempt}; retrying", attempt),
                ct).ConfigureAwait(false);
        }
        catch (LlmStreamErrorException lex) when (RetryPolicy.IsTransient(lex, out _))
        {
            // #270: the transient budget (minutes-scale for 429s) is exhausted.
            // Degrade instead of dying terminally: persist whatever streamed
            // before the failure and end the run gracefully, so the session
            // stays resumable and (sub-)callers receive partial output.
            AssistantMessage degraded = lastPartial ?? AssistantMessage.Empty(session.Session.Id, model.Id);
            logger.LogWarning(
                lex, "LLM stream budget exhausted on turn {Turn}; degrading with partial output ({Parts} parts)", turn, degraded.Parts.Count);
            if (degraded.Parts.Count > 0)
            {
                await session.AppendMessageAsync(degraded, ct).ConfigureAwait(false);
                tokenTracker.RecordAppendedMessage(degraded);
            }
            await eventBus.PublishAsync(new MessageEndEvent(degraded), ct).ConfigureAwait(false);
            await eventBus.PublishAsync(
                new TurnEndEvent(degraded, Array.Empty<ToolResultMessage>(), session.Session.Id), ct).ConfigureAwait(false);
            // #259: single collapsed-card error for the exhausted budget —
            // per-attempt publishes are suppressed above, so this is the only
            // AgentErrorEvent for the storm (never N duplicates).
            await eventBus.PublishAsync(new AgentErrorEvent(lex.Message, lex.Details), ct).ConfigureAwait(false);
            return new TurnStepResult(truncationFallback, EndRun: true);
        }
        catch (LlmStreamErrorException lex)
        {
            // Terminal provider error — same outcome as the pre-extraction
            // inline early-return: fail the run without appending the
            // partial message or publishing turn/agent end events.
            return new TurnStepResult(truncationFallback, EndRun: true, RunFailure: lex.Message);
        }

        var partial = streamed.Partial;
        var toolCalls = streamed.ToolCalls;
        var malformedCalls = streamed.MalformedCalls;
        Usage? finalUsage = streamed.FinalUsage;
        var stopReason = streamed.StopReason;

        await session.AppendMessageAsync(partial, ct).ConfigureAwait(false);
        // B3: feed the running token-estimate cache so ShouldCompact stays O(1).
        tokenTracker.RecordAppendedMessage(partial);
        if (finalUsage != null)
        {
            tokenTracker.RecordTurnUsage(finalUsage);
            // #653: the model that just made this call is the only thing that
            // knows what it cost, so its own rate table rides along with the
            // usage. The context folds and publishes the total; no renderer
            // prices anything (CostPricedInCoreRules).
            await session.UpdateStatsAsync(finalUsage, model.Pricing, ct).ConfigureAwait(false);
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
        logger.LogDebug("Turn {Turn}: toolCalls={ToolCalls} malformed={Malformed} stopReason={StopReason}", turn, toolCalls.Count, malformedCalls.Count, stopReason);
        if ((toolCalls.Count == 0 && malformedCalls.Count == 0) || stopReason == StopReason.Aborted)
        {
            logger.LogDebug("Turn {Turn} end (no tool calls)", turn);
            await eventBus.PublishAsync(
                new TurnEndEvent(partial, Array.Empty<ToolResultMessage>(), session.Session.Id), ct).ConfigureAwait(false);
            // #404: a capped text-only turn is a limit stop, not a finish
            // (the #403 shape on the budget axis). Cancel still wins: an
            // aborted stream ends as a cancel, never as a cap.
            if (budget is not null && stopReason != StopReason.Aborted && budget.CheckCap() is { } earlyHit)
                return new TurnStepResult(truncationFallback, EndRun: true, Limit: earlyHit);
            return new TurnStepResult(truncationFallback, EndRun: true);
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

        logger.LogDebug("Turn {Turn} end (with tool results)", turn);
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
        if (budget is not null && budget.CheckCap() is { } budgetHit)
        {
            logger.LogInformation("Agent hit budget cap ({Limit})", budgetHit);
            return new TurnStepResult(truncationFallback, EndRun: true, Limit: budgetHit);
        }

        // 11. Max steps — also honoured after a terminal stop reason.
        if (MaxStepsBehavior.IsExhausted(turn, agent))
        {
            logger.LogInformation("Agent reached max steps ({MaxSteps})", agent.MaxSteps);
            // #403: EndRun alone cannot say WHY. Without this the loop published
            // the same terminal event as a run that finished its work, and
            // `SessionStatus.Done` was the user's answer for a run that was cut
            // off with steps left to take. The limit is the fact; the bool is
            // only the shape the loop iterates on.
            return new TurnStepResult(truncationFallback, EndRun: true, Limit: RunLimitKind.MaxSteps);
        }

        if (runEndsAfterExecution)
        {
            logger.LogDebug("Turn {Turn}: terminal stop reason ({StopReason}) with executed tool calls — ending run", turn, stopReason);
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
    ///     before the throw, so budget exhaustion (#270) can degrade with the
    ///     last attempt's output instead of losing it.
    /// </remarks>
    private async Task<TurnStreamResult> ConsumeTurnStreamAsync(
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
        logger.LogDebug("Message start: turn={Turn}", turn);
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

        try
        {
            await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
            {
                // LogTrace is the most frequent log call in the hot path (one per
                // stream event = potentially thousands per turn). Guarding it with
                // IsEnabled avoids the params object?[] array allocation that
                // LogTrace incurs even when trace logging is off.
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Stream event: {EventType}", evt.GetType().Name);
                }
                switch (evt)
                {
                    case TextDeltaEvent td:
                        // Flush any pending thinking before starting/continuing a text run.
                        partial = FlushThinking(coalescer, partial);
                        coalescer.AppendTextDelta(td.Delta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        budget?.RecordOutputBytes(Encoding.UTF8.GetByteCount(td.Delta));
                        break;

                    case ThinkingDeltaEvent thd:
                        // Flush any pending text before starting/continuing a thinking run.
                        partial = FlushText(coalescer, partial);
                        coalescer.AppendThinkingDelta(thd.Delta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        budget?.RecordOutputBytes(Encoding.UTF8.GetByteCount(thd.Delta));
                        break;

                    case ToolCallStartEvent tcs:
                        partial = FlushAll(coalescer, partial);
                        coalescer.StartToolCall(tcs.Id, tcs.ToolName);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        break;

                    case ToolCallDeltaEvent tcd:
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace("ToolCallDelta id={Id} argsDelta={Args}", tcd.Id, tcd.ArgsDelta);
                        }
                        coalescer.AppendToolCallDelta(tcd.Id, tcd.ArgsDelta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        budget?.RecordOutputBytes(Encoding.UTF8.GetByteCount(tcd.ArgsDelta));
                        break;

                    case StepFinishEvent sf:
                    {
                        StepOutcome outcome = await FinalizeStepAsync(sf, coalescer, partial, malformedCalls, ct).ConfigureAwait(false);
                        partial = outcome.Partial;
                        toolCalls.AddRange(outcome.MaterializedCalls);
                        finalUsage = outcome.FinalUsage;
                        stopReason = outcome.StopReason;
                        break;
                    }

                    case ErrorEvent err:
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
                        // exhausted (see the catch below), zero on recovery.
                        if (!ProviderErrors.IsTransient(err.Kind))
                        {
                            await eventBus.PublishAsync(new AgentErrorEvent(err.Message, err.Exception), ct).ConfigureAwait(false);
                        }

                        throw new LlmStreamErrorException(err);
                }

                // #404: the output-size cap is enforced on the delta path —
                // stop pulling the stream instead of buffering the rest of
                // the response. Token/spend fold in after the stream, so only
                // the output axis can newly trip here; the post-stream check
                // reports the limit kind.
                if (budget is not null && budget.CheckCap() == RunLimitKind.MaxOutputBytes)
                    break;
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

        partial = AppendMalformedPlaceholders(partial, malformedCalls);

        logger.LogDebug("Message end: turn={Turn} stopReason={StopReason}", turn, stopReason);
        await eventBus.PublishAsync(new MessageEndEvent(partial), ct).ConfigureAwait(false);

        return new TurnStreamResult(partial, toolCalls, malformedCalls, finalUsage, stopReason);
    }

    /// <summary>Flush a pending text run into the message (ROP-C flush choreography).</summary>
    private static AssistantMessage FlushText(StreamingCoalescer coalescer, AssistantMessage partial) =>
        coalescer.HasPendingText ? partial.AppendText(coalescer.FlushText()) : partial;

    /// <summary>Flush a pending thinking run into the message.</summary>
    private static AssistantMessage FlushThinking(StreamingCoalescer coalescer, AssistantMessage partial) =>
        coalescer.HasPendingThinking ? partial.AppendThinking(coalescer.FlushThinking()) : partial;

    /// <summary>
    ///     Flush both buffer kinds in wire order (text first, then thinking) —
    ///     the sequence every non-delta arm and the abort path must perform
    ///     (ROP-C П.3: five copies of this dance collapse to one helper).
    /// </summary>
    private static AssistantMessage FlushAll(StreamingCoalescer coalescer, AssistantMessage partial)
    {
        AssistantMessage flushedText = FlushText(coalescer, partial);
        return FlushThinking(coalescer, flushedText);
    }

    /// <summary>Publish one MessageUpdateEvent for a coalesced stream delta.</summary>
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
        AssistantMessage partial,
        List<MalformedToolCall> malformedCalls,
        CancellationToken ct)
    {
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
            logger.LogWarning(
                "Malformed JSON arguments for tool call {CallId} ({ToolName}); raw tail: {ArgsTail}",
                malformed.Id, malformed.ToolName, malformed.RawArgsTail);
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
}
