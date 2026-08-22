using Harbor.Abstractions.Sessions;
using Harbor.Core.Resilience;
using Harbor.Core.Resources;
using Harbor.Core.Sessions;
using Harbor.Core.Telemetry;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Agents;
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
    private readonly ToolDispatcher _toolDispatcher;
    private readonly IToolRegistry _tools;

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
        ILogger<AgentLoop> logger)
    {
        _providers = providers;
        _tools = tools;
        _agents = agents;
        _promptBuilder = promptBuilder;
        _compaction = compaction;
        _tokenTracker = tokenTracker;
        _retryPolicy = retryPolicy;
        _eventBus = eventBus;
        _permissions = permissions;
        _messageConverter = messageConverter;
        _logger = logger;
        _toolDispatcher = new ToolDispatcher(tools, permissions, eventBus, logger);
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
    public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
    {
        using var activity = HarborTelemetry.Source.StartActivity("Agent.Run");
        activity?.SetTag(GenAiTags.AgentName, agent.Name.Value);
        activity?.SetTag(GenAiTags.RequestModel, agent.Model);
        try
        {
            _logger.LogInformation(CoreResources.GetLog("AgentLoopStarting"), agent.Name.Value);

            // Resolve the model once up front so the context window can be carried
            // on AgentStartEvent (renderers need it to show context usage).
            var providerIdResult = ProviderId.TryCreate(agent.ProviderId);
            if (providerIdResult.IsFailure)
                return Result.Failure(providerIdResult.Error);

            var clientResult = _providers.GetClient(providerIdResult.Value);
            if (clientResult.IsFailure)
                return Result.Failure(clientResult.Error);

            var client = clientResult.Value;
            var modelsResult = await client.GetModelsAsync(ct).ConfigureAwait(false);
            if (modelsResult.IsFailure)
                return Result.Failure(modelsResult.Error);

            var model = FindModel(modelsResult.Value, agent.Model);
            if (model is null)
                return Result.Failure($"Model '{agent.Model}' not found in provider '{agent.ProviderId}'.");

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
                await _eventBus.PublishAsync(new TurnStartEvent(turn), ct).ConfigureAwait(false);

                IReadOnlyList<AgentMessage> turnMessages = session.Messages;

                // 2. Compaction check. Never retried once the fallback is
                // engaged — the summarizer just failed, so every turn after
                // the failure derives its request from truncation below.
                if (!truncationFallback && _tokenTracker.ShouldCompact(turnMessages, model))
                {
                    using var compactionActivity = HarborTelemetry.Source.StartActivity("Compaction");
                    _logger.LogInformation("Compaction triggered for session {SessionId}", session.Session.Id);
                    await _eventBus.PublishAsync(new CompactionStartedEvent(session.Session.Id), ct).ConfigureAwait(false);
                    var compactionResult = await _compaction.CompactAsync(session.Session.Id, turnMessages, model, ct).ConfigureAwait(false);

                    // Railway Oriented Programming: Match dispatches to the
                    // success or failure branch without an explicit
                    // `if (result.IsSuccess)` check, making the
                    // happy-path/error-path split structural rather than
                    // control-flow.
                    await compactionResult.Match(
                        async result =>
                        {
                            await session.AppendMessageAsync(result.SummaryMessage, ct).ConfigureAwait(false);
                            await _eventBus.PublishAsync(new CompactionCompletedEvent(
                                session.Session.Id,
                                result.Summary,
                                result.PrunedMessageCount,
                                result.TokensSaved,
                                result.Duration), ct).ConfigureAwait(false);
                        },
                        error =>
                        {
                            // Never continue silently with a known-invalid
                            // (overfull) context: publish the failure and
                            // switch to strict tail truncation for this and
                            // all subsequent requests.
                            _logger.LogWarning("Compaction failed: {Error}. Falling back to truncation.", error);
                            truncationFallback = true;
                            return _eventBus.PublishAsync(new CompactionFailedEvent(session.Session.Id, error), CancellationToken.None);
                        }).ConfigureAwait(false);
                }

                // A compaction failure — including one earlier in this very
                // turn — leaves the history known-overfull: derive THIS
                // request from a strictly reduced recent tail instead.
                if (truncationFallback)
                {
                    turnMessages = CompactionService.TruncateToFitStrict(session.Messages, model, _tokenTracker);
                }

                // 3. Build system prompt
                var tools = _tools.ResolveTools(agent.Name.Value, agent.Permission);
                var promptContext = new SystemPromptContext(
                    agent,
                    model,
                    tools,
                    Array.Empty<ContextFile>(),
                    Array.Empty<SkillDescriptor>(),
                    null,
                    session.Session.Directory);
                string systemPrompt = await _promptBuilder.BuildAsync(promptContext, ct).ConfigureAwait(false);

                // 4. Convert messages — the truncated view after a compaction
                // failure, the full history otherwise.
                var llmMessages = _messageConverter.ToLlmMessages(turnMessages);

                // 5. Build request — size the ToolDefinition array directly instead of LINQ Select().ToList().
                var toolDefs = BuildToolDefinitions(tools);
                var request = new LlmRequest(
                    agent.Model,
                    llmMessages,
                    systemPrompt,
                    toolDefs,
                    MaxOutputTokens: model.MaxOutputTokens,
                    Temperature: agent.Temperature,
                    ReasoningEffort: agent.ReasoningEffort);

                // 6. Stream LLM — coalesce consecutive text/thinking deltas in pooled StringBuilders
                //    to avoid creating a new array per delta (the previous AppendText per-delta approach
                //    was O(n²) in array allocations).
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
                                if (coalescer.HasPendingThinking)
                                {
                                    partial = partial.AppendThinking(coalescer.FlushThinking());
                                }
                                coalescer.AppendTextDelta(td.Delta);
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case ThinkingDeltaEvent thd:
                                // Flush any pending text before starting/continuing a thinking run.
                                if (coalescer.HasPendingText)
                                {
                                    partial = partial.AppendText(coalescer.FlushText());
                                }
                                coalescer.AppendThinkingDelta(thd.Delta);
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case ToolCallStartEvent tcs:
                                // Flush any pending text/thinking before tracking the tool call.
                                if (coalescer.HasPendingText)
                                {
                                    partial = partial.AppendText(coalescer.FlushText());
                                }
                                if (coalescer.HasPendingThinking)
                                {
                                    partial = partial.AppendThinking(coalescer.FlushThinking());
                                }
                                coalescer.StartToolCall(tcs.Id, tcs.ToolName);
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case ToolCallDeltaEvent tcd:
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.LogTrace("ToolCallDelta id={Id} argsDelta={Args}", tcd.Id, tcd.ArgsDelta);
                                }
                                coalescer.AppendToolCallDelta(tcd.Id, tcd.ArgsDelta);
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case StepFinishEvent sf:
                                // Flush any pending text/thinking before finalizing.
                                if (coalescer.HasPendingText)
                                {
                                    partial = partial.AppendText(coalescer.FlushText());
                                }
                                if (coalescer.HasPendingThinking)
                                {
                                    partial = partial.AppendThinking(coalescer.FlushThinking());
                                }

                                // Materialize any tool calls accumulated from Start/Delta fragments.
                                // Calls with un-parseable args JSON are reported via malformedCalls
                                // and excluded from the executable list.
                                var materializedCalls = coalescer.MaterializeToolCalls(malformedCalls);
                                foreach (var tc in materializedCalls)
                                {
                                    partial = partial.AppendToolCall(tc);
                                }
                                toolCalls.AddRange(materializedCalls);

                                finalUsage = sf.Usage;
                                stopReason = StopReasonJsonConverter.Parse(sf.FinishReason);
                                partial = partial.WithFinish(stopReason, finalUsage ?? new Usage(0, 0));
                                // Forward StepFinish to the bus so status bars / views can
                                // tally token usage. The event is otherwise swallowed here.
                                await _eventBus.PublishAsync(new MessageUpdateEvent(sf, partial), ct).ConfigureAwait(false);
                                break;

                            case ErrorEvent err:
                                // Discard any per-tool-call pooled StringBuilders before early-returning.
                                coalescer.DiscardPendingToolCalls();
                                await _eventBus.PublishAsync(new AgentErrorEvent(err.Message, err.Exception), ct).ConfigureAwait(false);
                                return Result.Failure(err.Message);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Flush any pending buffers before recording the aborted finish.
                    if (coalescer.HasPendingText)
                    {
                        partial = partial.AppendText(coalescer.FlushText());
                    }
                    if (coalescer.HasPendingThinking)
                    {
                        partial = partial.AppendThinking(coalescer.FlushThinking());
                    }
                    // Discard any per-tool-call pooled StringBuilders — otherwise
                    // cancellation mid-stream would leak them.
                    coalescer.DiscardPendingToolCalls();
                    partial = partial.WithFinish(StopReason.Aborted, finalUsage ?? new Usage(0, 0));
                    // Align the loop state with the aborted finish: previously the
                    // stale stopReason/toolCalls from an earlier StepFinish could
                    // cause tool execution AFTER cancellation.
                    stopReason = StopReason.Aborted;
                    toolCalls.Clear();
                    malformedCalls.Clear();
                }

                // Surface malformed tool calls (C4): keep the assistant message's
                // wire shape consistent by appending a placeholder part per call —
                // every tool_call must be answered by a tool_result — while the
                // error result below tells the model its args were un-parseable.
                for (int i = 0; i < malformedCalls.Count; i++)
                {
                    var malformed = malformedCalls[i];
                    _logger.LogWarning(
                        "Malformed JSON arguments for tool call {CallId} ({ToolName}); raw tail: {ArgsTail}",
                        malformed.Id, malformed.ToolName, malformed.RawArgsTail);
                    partial = partial.AppendToolCall(new ToolCallPart(malformed.Id, malformed.ToolName, EmptyJsonArgs()));
                }

                _logger.LogDebug("Message end: turn={Turn} stopReason={StopReason}", turn, stopReason);
                await _eventBus.PublishAsync(new MessageEndEvent(partial), ct).ConfigureAwait(false);
                await session.AppendMessageAsync(partial, ct).ConfigureAwait(false);
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
                        new TurnEndEvent(partial, Array.Empty<ToolResultMessage>()), ct).ConfigureAwait(false);
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

                _logger.LogDebug("Turn {Turn} end (with tool results)", turn);
                await _eventBus.PublishAsync(
                    new TurnEndEvent(partial, new[] { toolResults }), ct).ConfigureAwait(false);

                // 9. Steering check — drain the whole queue at the turn boundary.
                while (session.SteeringQueue.Reader.TryRead(out var steerMsg))
                {
                    await session.AppendMessageAsync(steerMsg, ct).ConfigureAwait(false);
                }

                // 10. Max steps — also honoured after a terminal stop reason.
                if (turn >= agent.MaxSteps)
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
                _logger.LogInformation("Agent run cancelled: agent={Agent}", agent.Name.Value);
                await _eventBus.PublishAsync(
                    new AgentEndEvent(SnapshotMessages(session.Messages), Cancelled: true), CancellationToken.None).ConfigureAwait(false);

                return Result.Failure("Agent run was cancelled.");
            }

            _logger.LogInformation("Agent loop completed: agent={Agent}", agent.Name.Value);
            await _eventBus.PublishAsync(
                new AgentEndEvent(SnapshotMessages(session.Messages)), ct).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            _logger.LogError(ex, CoreResources.GetError("AgentFailed"), ex.Message);
            await _eventBus.PublishAsync(new AgentErrorEvent(ex.Message, ex.ToString()), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(ex.Message);
        }
    }

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
                toolCalls, session, partial, agent, ct).ConfigureAwait(false);
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
