using System.Globalization;
using Harbor.Diagnostics;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents.Pipeline;
using Harbor.Application.Resilience;
using Harbor.Application.Resources;
using Harbor.Application.Sessions;
using Harbor.Application.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Agents;
/// <summary>
///     Default agent loop. Implements Chain of Responsibility pattern (GOF):
///     prompt → LLM stream → tool execution → next turn → (compaction if needed) → repeat.
///     <para>
///         <b>Decomposition (Task R32):</b> the streaming-buffer coalescing
///         and tool-execution dispatch were extracted into:
///         <list type="bullet">
///             <item><see cref="StreamingCoalescer" /> — text/thinking/tool-call buffer management</item>
///             <item><see cref="ToolDispatcher" /> — parallel/sequential tool execution + permission gating</item>
///         </list>
///         The loop itself now focuses on turn orchestration, event
///         publishing, and compaction checks.
///     </para>
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly IAgentRegistry _agents;
    private readonly ICompactionService _compaction;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentLoop> _logger;
    private readonly MessageConverter _messageConverter;
    private readonly IPermissionService _permissions;
    private readonly IRetryPolicy _retryPolicy;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IProviderRegistry _providers;
    private readonly ITokenTracker _tokenTracker;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IToolRegistry _tools;
    private readonly AgentPipeline _pipeline;
    private readonly CompactionBehavior _compactionBehavior;
    private readonly SteeringDrainBehavior _steering;
    private readonly IMcpRegistry? _mcpRegistry;
    private readonly IMetrics _metrics;
    private readonly ITracer _tracer;

    // C7: bounded retry budget for the LLM streaming call site only.
    private static readonly RetryOptions StreamRetryOptions = new(MaxAttempts: 3, BaseDelay: TimeSpan.FromSeconds(1), UseJitter: true);

    /// <summary>
    ///     Construct an <see cref="AgentLoop" /> wired to the supplied services.
    /// </summary>
    public AgentLoop(
        IProviderRegistry providers,
        IToolRegistry tools,
        IAgentRegistry agents,
        ISystemPromptBuilder promptBuilder,
        ICompactionService compaction,
        ITokenTracker tokenTracker,
        IRetryPolicy retryPolicy,
        IEventBus eventBus,
        IPermissionService permissions,
        MessageConverter messageConverter,
        ILogger<AgentLoop> logger,
        IMetrics? metrics = null,
        ITracer? tracer = null,
        IToolDispatcher? toolDispatcher = null,
        IMcpRegistry? mcpRegistry = null)
    {
        _providers = providers;
        _tools = tools;
        _agents = agents;
        // Ф6/A2: memoize prompt builds — same (agent, model, tools, context)
        // hash returns the cached string instead of re-running the ~180-line
        // template assembly every turn.
        _promptBuilder = new CachingSystemPromptBuilder(promptBuilder);
        _compaction = compaction;
        _tokenTracker = tokenTracker;
        _retryPolicy = retryPolicy;
        _eventBus = eventBus;
        _permissions = permissions;
        _messageConverter = messageConverter;
        _logger = logger;
        _metrics = metrics ?? NullMetrics.Instance;
        _tracer = tracer ?? NullTracer.Instance;
        // ROP-C П.5: the dispatcher is injected via DI when composed by the host,
        // while tests and benchmarks fall back to a locally built one. That
        // fallback uses a NullLogger because the loop's own typed logger must
        // not be lent out under a foreign category (S6672).
        _toolDispatcher = toolDispatcher
            ?? new ToolDispatcher(tools, permissions, eventBus, NullLogger<ToolDispatcher>.Instance);
        // §3.5 pipeline: run-level cross-cutting concerns are middleware over the
        // whole run; per-turn behaviors (compaction, steering, max steps) are
        // extracted classes the core loop calls each turn. Behaviors share the
        // loop's logger so log categories stay identical to pre-extraction.
        _pipeline = new AgentPipeline(
        [
            new LoggingBehavior(logger),
            new PermissionCheckBehavior(logger),
        ]);
        _compactionBehavior = new CompactionBehavior(compaction, tokenTracker, eventBus, _metrics, logger);
        _steering = new SteeringDrainBehavior(tokenTracker, logger);
        // ROP-D Z3: MCP server instructions flow into the system prompt when a
        // registry is composed in; tests without one keep the section absent.
        _mcpRegistry = mcpRegistry;
    }

    /// <summary>
    ///     Run the agent loop to completion: prompt → LLM stream → tool execution → next turn,
    ///     repeating until either no tool calls are emitted or <see cref="AgentDefinition.MaxSteps" />
    ///     is reached. Compaction runs at the start of each turn if the token estimator says so.
    /// </summary>
    /// <param name="session">The session context for this run.</param>
    /// <param name="agent">The agent definition driving the loop.</param>
    /// <param name="ct">Cancellation token used to abort the run at the next safe boundary.</param>
    /// <returns>Success on normal completion, or failure with an error message.</returns>
    public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
    {
        // §3.5: the run enters the behavior pipeline; the original turn loop is the
        // terminal handler (RunCoreAsync).
        return _pipeline.HandleAsync(new PromptRequest(session, agent), RunCoreAsync, ct);
    }

    /// <summary>
    ///     Terminal pipeline handler: the turn loop itself — prompt → LLM stream →
    ///     tool execution → next turn, repeating until no tool calls are emitted,
    ///     the step budget is exhausted, or the run is cancelled.
    /// </summary>
    private async Task<Result> RunCoreAsync(PromptRequest run, CancellationToken ct)
    {
        ISessionContext session = run.Session;
        AgentDefinition agent = run.Agent;
        using var activity = HarborTelemetry.Source.StartActivity("Agent.Run");
        activity?.SetTag(GenAiTags.AgentName, agent.Name.Value);
        activity?.SetTag(GenAiTags.RequestModel, agent.Model);
        try
        {
            // Resolve the model once up front so the context window can be carried
            // on AgentStartEvent (renderers need it to show context usage).
            // ROP-C П.1-П.3/П.7: the TryCreate → GetClient → catalog chain rides
            // one Bind railway with a single failure exit; the TTL-cached catalog
            // lives in the shared provider registry, not per-loop.
            var resolved = await ResolveModelAsync(agent, ct).ConfigureAwait(false);
            if (resolved.IsFailure) // §4.6-ok: единственный выход Bind-рельсы setup'а (rop-final-mile L1).
                return Result.Failure(resolved.Error);

            var (client, model) = resolved.Value;

            await _eventBus.PublishAsync(new AgentStartEvent(session.Session.Id, SnapshotMessages(session.Messages), model), ct).ConfigureAwait(false);

            int turn = 0;
            // Set when LLM-based compaction fails; the CURRENT and every
            // subsequent turn then build their request from a strictly
            // reduced tail of the history instead of continuing with a
            // known-overfull context.
            bool truncationFallback = false;
            while (!ct.IsCancellationRequested)
            {
                turn++;
                _logger.LogDebug("Turn {Turn} start: agent={Agent} model={Model}", turn, agent.Name.Value, agent.Model);
                await _eventBus.PublishAsync(new TurnStartEvent(turn, session.Session.Id), ct).ConfigureAwait(false);

                // The compacted view of the history, not the raw append-only
                // list: after a summary was produced, ShouldCompact and the
                // request both see [summary] + kept tail, so compaction does
                // not re-trigger on every subsequent turn.
                IReadOnlyList<AgentMessage> turnMessages = CompactionService.MaterializeCompactedView(session.Messages);

                // 2. Compaction check + truncation fallback — the per-turn
                // CompactionBehavior owns threshold check, summarization,
                // events/metrics and the fallback decision (§3.5).
                CompactionOutcome compactionOutcome = await _compactionBehavior
                    .BeforeTurnAsync(session, turnMessages, model, truncationFallback, ct)
                    .ConfigureAwait(false);
                turnMessages = compactionOutcome.TurnMessages;
                truncationFallback = compactionOutcome.TruncationFallback;

                // A compaction failure — including one earlier in this very
                // turn — leaves the history known-overfull: derive THIS
                // request from a strictly reduced recent tail instead.
                if (truncationFallback)
                {
                    turnMessages = CompactionService.TruncateToFitStrict(turnMessages, model, _tokenTracker);
                    _metrics.Histogram(
                        "session.context.size", _tokenTracker.EstimateTokens(turnMessages),
                        new KeyValuePair<string, object?>("context.phase", "truncated"),
                        new KeyValuePair<string, object?>("session.id", session.Session.Id));
                }

                // 3. Build system prompt
                var tools = _tools.ResolveTools(agent.Name.Value, agent.Permission);
                // Cached per-directory: avoids File.Exists/Directory.GetFiles on every turn (50x regression).
                var (contextFiles, skills) = WorkspaceContextSource.GetOrLoadCached(session.Session.Directory);
                var promptContext = new SystemPromptContext(
                    agent,
                    model,
                    tools,
                    contextFiles,
                    skills,
                    WorkspaceContextSource.FormatMcpInstructions(_mcpRegistry?.GetInstructions()),
                    session.Session.Directory);
                string systemPrompt = await _promptBuilder.BuildAsync(promptContext, ct).ConfigureAwait(false);

                // 4. Convert messages — the truncated view after a compaction
                // failure, the full history otherwise.
                var llmMessages = _messageConverter.ToLlmMessages(turnMessages);

                // 5. Build request — size the ToolDefinition array directly instead of LINQ Select().ToList().
                var toolDefs = BuildToolDefinitions(tools);
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
                //    whole stream attempt up to MaxAttempts times; fatal failures
                //    (auth/quota, caller cancellation) propagate immediately.
                //    Each attempt rebuilds its accumulators and re-publishes
                //    MessageStart → MessageUpdate… → MessageEnd for the turn,
                //    mirroring a fresh streaming pass.
                TurnStreamResult streamed;
                try
                {
                    streamed = await _retryPolicy.ExecuteAsync(
                        attemptCt => ConsumeTurnStreamAsync(client, request, session, model, turn, attemptCt),
                        StreamRetryOptions,
                        (ex, attempt) => _logger.LogWarning(
                            ex, "Transient LLM stream failure on attempt {Attempt}; retrying", attempt),
                        ct).ConfigureAwait(false);
                }
                catch (LlmStreamErrorException lex)
                {
                    // Terminal provider error event — same outcome as the previous
                    // inline early-return: fail the run without appending the
                    // partial message or publishing turn/agent end events.
                    return Result.Failure(lex.Message);
                }

                var partial = streamed.Partial;
                var toolCalls = streamed.ToolCalls;
                var malformedCalls = streamed.MalformedCalls;
                Usage? finalUsage = streamed.FinalUsage;
                var stopReason = streamed.StopReason;

                await session.AppendMessageAsync(partial, ct).ConfigureAwait(false);
                // B3: feed the running token-estimate cache so ShouldCompact stays O(1).
                _tokenTracker.RecordAppendedMessage(partial);
                if (finalUsage != null)
                {
                    _tokenTracker.RecordTurnUsage(finalUsage);
                    await session.UpdateStatsAsync(finalUsage, ct).ConfigureAwait(false);
                }

                // 7. Turn-end decision. A run ends when the turn produced no
                // tool activity, or the stream was aborted mid-flight (never
                // execute tools for a cancelled run).
                _logger.LogDebug("Turn {Turn}: toolCalls={ToolCalls} malformed={Malformed} stopReason={StopReason}", turn, toolCalls.Count, malformedCalls.Count, stopReason);
                if ((toolCalls.Count == 0 && malformedCalls.Count == 0) || stopReason == StopReason.Aborted)
                {
                    _logger.LogDebug("Turn {Turn} end (no tool calls)", turn);
                    await _eventBus.PublishAsync(
                        new TurnEndEvent(partial, Array.Empty<ToolResultMessage>(), session.Session.Id), ct).ConfigureAwait(false);
                    break;
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
                _tokenTracker.RecordAppendedMessage(toolResults);

                // Ф2/B2: mid-run steering injection INSIDE the turn. Drained
                // right AFTER the tool results are persisted (never between
                // the assistant tool_calls and their results — providers
                // require that adjacency) so the NEXT LLM request of THIS run
                // already carries the steering, not just the next turn.
                await _steering.DrainAsync(session, ct).ConfigureAwait(false);

                _logger.LogDebug("Turn {Turn} end (with tool results)", turn);
                await _eventBus.PublishAsync(
                    new TurnEndEvent(partial, new[] { toolResults }, session.Session.Id), ct).ConfigureAwait(false);

                // 9. Boundary steering drain — kept for runs that reach max
                // steps or a terminal stop reason right after execution; on
                // the normal path it is a no-op (B2 drained above).
                await _steering.DrainAsync(session, ct).ConfigureAwait(false);

                // 10. Max steps — also honoured after a terminal stop reason.
                if (MaxStepsBehavior.IsExhausted(turn, agent))
                {
                    _logger.LogInformation("Agent reached max steps ({MaxSteps})", agent.MaxSteps);
                    break;
                }

                if (runEndsAfterExecution)
                {
                    _logger.LogDebug("Turn {Turn}: terminal stop reason ({StopReason}) with executed tool calls — ending run", turn, stopReason);
                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                // A cancelled run is NOT a successful run: report failure so callers
                // (and WaitForIdleAsync consumers) can distinguish it from normal
                // completion. The AgentEndEvent carries Cancelled=true so renderers
                // can reflect the aborted state instead of a clean finish.
                _logger.LogInformation("Agent run cancelled: session={SessionId} agent={Agent}", session.Session.Id, agent.Name.Value);
                await _eventBus.PublishAsync(
                    new AgentEndEvent(SnapshotMessages(session.Messages), Cancelled: true), CancellationToken.None).ConfigureAwait(false);

                return Result.Failure("Agent run was cancelled.");
            }

            _logger.LogInformation("Agent loop completed: session={SessionId} agent={Agent}", session.Session.Id, agent.Name.Value);
            await _eventBus.PublishAsync(
                new AgentEndEvent(SnapshotMessages(session.Messages)), ct).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            // O1: keep the localized message AND the correlation key in one record.
            string failure = string.Format(CultureInfo.InvariantCulture, CoreResources.GetError("AgentFailed"), ex.Message);
            _logger.LogError(ex, "Agent run failed: session={SessionId} error={Error}", session.Session.Id, failure);
            await _eventBus.PublishAsync(new AgentErrorEvent(ex.Message, ex.ToString()), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    ///     Resolve the provider id, LLM client and concrete model for this run.
    ///     Errors are routed structurally by the Bind chain: any step failing
    ///     short-circuits to the single <c>IsFailure</c> exit. The "model may be
    ///     absent" case is expressed as <see cref="Maybe{T}"/> → ToResult rather
    ///     than a null-check convention.
    /// </summary>
    private async Task<Result<(ILlmClient Client, ModelInfo Model)>> ResolveModelAsync(
        AgentDefinition agent,
        CancellationToken ct)
    {
        Result<(ILlmClient Client, IReadOnlyList<ModelInfo> Catalog)> provider =
            await ProviderId.TryCreate(agent.ProviderId)
                .Bind(async id =>
                {
                    var clientResult = _providers.GetClient(id);
                    if (clientResult.IsFailure) // §4.6-ok: тело рельсы ResolveModelAsync — ранний выход внутри Bind-лямбды.
                        return Result.Failure<(ILlmClient, IReadOnlyList<ModelInfo>)>(clientResult.Error);

                    var models = await _providers.GetModelsCachedAsync(id, ct).ConfigureAwait(false);
                    return models.IsSuccess
                        ? Result.Success((clientResult.Value, models.Value))
                        : Result.Failure<(ILlmClient, IReadOnlyList<ModelInfo>)>(models.Error);
                })
                .ConfigureAwait(false);

        if (provider.IsFailure) // §4.6-ok: Match-граница рельсы — один выход вместо трёх if.
        {
            return Result.Failure<(ILlmClient, ModelInfo)>(provider.Error);
        }

        var (client, models) = provider.Value;
        return Maybe.From(FindModel(models, agent.Model))
            .ToResult($"Model '{agent.Model}' not found in provider '{agent.ProviderId}'.")
            .Map(m => (client, m));
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
    /// </remarks>
    private async Task<TurnStreamResult> ConsumeTurnStreamAsync(
        ILlmClient client,
        LlmRequest request,
        ISessionContext session,
        ModelInfo model,
        int turn,
        CancellationToken ct)
    {
        var partial = AssistantMessage.Empty(session.Session.Id, model.Id);
        _logger.LogDebug("Message start: turn={Turn}", turn);
        await _eventBus.PublishAsync(new MessageStartEvent(partial), ct).ConfigureAwait(false);

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
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Stream event: {EventType}", evt.GetType().Name);
                }
                switch (evt)
                {
                    case TextDeltaEvent td:
                        // Flush any pending thinking before starting/continuing a text run.
                        partial = FlushThinking(coalescer, partial);
                        coalescer.AppendTextDelta(td.Delta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        break;

                    case ThinkingDeltaEvent thd:
                        // Flush any pending text before starting/continuing a thinking run.
                        partial = FlushText(coalescer, partial);
                        coalescer.AppendThinkingDelta(thd.Delta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        break;

                    case ToolCallStartEvent tcs:
                        partial = FlushAll(coalescer, partial);
                        coalescer.StartToolCall(tcs.Id, tcs.ToolName);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
                        break;

                    case ToolCallDeltaEvent tcd:
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("ToolCallDelta id={Id} argsDelta={Args}", tcd.Id, tcd.ArgsDelta);
                        }
                        coalescer.AppendToolCallDelta(tcd.Id, tcd.ArgsDelta);
                        await PublishUpdateAsync(evt, partial, ct).ConfigureAwait(false);
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
                        // Discard any per-tool-call pooled StringBuilders before
                        // propagating the terminal error (same as the previous
                        // inline early-return out of RunAsync).
                        coalescer.DiscardPendingToolCalls();
                        await _eventBus.PublishAsync(new AgentErrorEvent(err.Message, err.Exception), ct).ConfigureAwait(false);
                        throw new LlmStreamErrorException(err);
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

        partial = AppendMalformedPlaceholders(partial, malformedCalls);

        _logger.LogDebug("Message end: turn={Turn} stopReason={StopReason}", turn, stopReason);
        await _eventBus.PublishAsync(new MessageEndEvent(partial), ct).ConfigureAwait(false);

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
        _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct);

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
            _logger.LogWarning(
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
            var executed = await _toolDispatcher.ExecuteAsync(
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
    ///     Placeholder args for a malformed tool call's assistant-side part.
    ///     The real arguments were un-parseable; the error tool_result carries
    ///     the diagnostics, while this keeps the tool_call ↔ tool_result
    ///     pairing providers require.
    /// </summary>
    private static JsonElement EmptyJsonArgs()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     Build the ToolDefinition array directly, avoiding the LINQ Select().ToList() allocation
    ///     (which allocates a delegate + iterator + List).
    /// </summary>
    private static ToolDefinition[] BuildToolDefinitions(IReadOnlyList<ToolDescriptor> tools)
    {
        if (tools.Count == 0)
        {
            return Array.Empty<ToolDefinition>();
        }

        var result = new ToolDefinition[tools.Count];
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            result[i] = new ToolDefinition(t.Name.Value, t.Description, t.Schema);
        }
        return result;
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
    ///     Materialize a snapshot list of the current session messages for events.
    /// </summary>
    private static List<AgentMessage> SnapshotMessages(IReadOnlyList<AgentMessage> messages)
    {
        var snapshot = new List<AgentMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            snapshot.Add(messages[i]);
        }
        return snapshot;
    }
}
