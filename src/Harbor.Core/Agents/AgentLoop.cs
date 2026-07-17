using System.Buffers;
using CommunityToolkit.HighPerformance.Buffers;
using Harbor.Abstractions.Extensions;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Agents;
/// <summary>
///     Default agent loop. Implements Chain of Responsibility pattern (GOF):
///     prompt → LLM stream → tool execution → next turn → (compaction if needed) → repeat.
///     Performance:
///     - Streaming deltas are coalesced in a pooled StringBuilder before being attached to the
///     partial message, reducing per-delta array allocations from O(n²) to O(n) per text run.
///     - Tool-definition arrays are sized directly instead of via LINQ Select().ToList().
///     - Tool names are interned via <see cref="StringPool" /> to deduplicate provider/tool strings.
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly IAgentRegistry _agents;
    private readonly ICompactionService _compaction;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentLoop> _logger;
    private readonly MessageConverter _messageConverter;
    private readonly IPermissionService _permissions;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly IProviderRegistry _providers;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly IToolRegistry _tools;

    /// <summary>
    ///     Construct an <see cref="AgentLoop" /> wired to the supplied services.
    /// </summary>
    /// <param name="providers">The provider registry for LLM clients.</param>
    /// <param name="tools">The tool registry for tool lookup and resolution.</param>
    /// <param name="agents">The agent registry for permission lookup.</param>
    /// <param name="promptBuilder">The system prompt builder.</param>
    /// <param name="compaction">The compaction service for context-window management.</param>
    /// <param name="tokenEstimator">The token estimator used by compaction.</param>
    /// <param name="eventBus">The event bus to publish agent events to.</param>
    /// <param name="permissions">The permission service for tool-call authorization.</param>
    /// <param name="messageConverter">The converter from domain messages to LLM messages.</param>
    /// <param name="logger">The logger.</param>
    public AgentLoop(
        IProviderRegistry providers,
        IToolRegistry tools,
        IAgentRegistry agents,
        ISystemPromptBuilder promptBuilder,
        ICompactionService compaction,
        ITokenEstimator tokenEstimator,
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
        _tokenEstimator = tokenEstimator;
        _eventBus = eventBus;
        _permissions = permissions;
        _messageConverter = messageConverter;
        _logger = logger;
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
        try
        {
            _logger.LogInformation("Agent loop starting: agent={Agent}", agent.Name.Value);
            await _eventBus.PublishAsync(new AgentStartEvent(session.Session.Id, SnapshotMessages(session.Messages)), ct).ConfigureAwait(false);

            int turn = 0;
            while (!ct.IsCancellationRequested)
            {
                turn++;
                _logger.LogDebug("Turn {Turn} start: agent={Agent} model={Model}", turn, agent.Name.Value, agent.Model);
                await _eventBus.PublishAsync(new TurnStartEvent(turn), ct).ConfigureAwait(false);

                // 1. Resolve model — Railway Oriented Programming style:
                //    Each step returns Result<T> and short-circuits on failure,
                //    threading the error through to the final return.
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

                // 2. Compaction check
                if (_compaction.ShouldCompact(session.Messages, model))
                {
                    _logger.LogInformation("Compaction triggered for session {SessionId}", session.Session.Id);
                    await _eventBus.PublishAsync(new CompactionStartedEvent(session.Session.Id), ct).ConfigureAwait(false);
                    var compactionResult = await _compaction.CompactAsync(session.Session.Id, session.Messages, model, ct).ConfigureAwait(false);

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
                            _logger.LogWarning("Compaction failed: {Error}", error);
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
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

                // 4. Convert messages
                var llmMessages = _messageConverter.ToLlmMessages(session.Messages);

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
                Usage? finalUsage = null;
                var stopReason = StopReason.Stop;

                using var textBuffer = StringBuilderPool.Rent(4096);
                using var thinkingBuffer = StringBuilderPool.Rent(1024);
                bool hasPendingText = false;
                bool hasPendingThinking = false;

                // Accumulator for tool calls streamed as Start + Delta fragments
                // (OpenAI-compatible providers never emit ToolCallEndEvent).
                // Holds PooledStringBuilder values so we can return each arg buffer to the pool
                // when the tool call is finalized on StepFinishEvent.
                var pendingToolCalls = new Dictionary<string, (string Name, StringBuilderPool.PooledStringBuilder Args)>(capacity: 4);
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
                                if (hasPendingThinking)
                                {
                                    partial = partial.AppendThinking(thinkingBuffer.ToString());
                                    thinkingBuffer.Builder.Clear();
                                    hasPendingThinking = false;
                                }
                                textBuffer.Builder.Append(td.Delta);
                                hasPendingText = true;
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case ThinkingDeltaEvent thd:
                                // Flush any pending text before starting/continuing a thinking run.
                                if (hasPendingText)
                                {
                                    partial = partial.AppendText(textBuffer.ToString());
                                    textBuffer.Builder.Clear();
                                    hasPendingText = false;
                                }
                                thinkingBuffer.Builder.Append(thd.Delta);
                                hasPendingThinking = true;
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case ToolCallStartEvent tcs:
                                // Flush any pending text/thinking before tracking the tool call.
                                if (hasPendingText)
                                {
                                    partial = partial.AppendText(textBuffer.ToString());
                                    textBuffer.Builder.Clear();
                                    hasPendingText = false;
                                }
                                if (hasPendingThinking)
                                {
                                    partial = partial.AppendThinking(thinkingBuffer.ToString());
                                    thinkingBuffer.Builder.Clear();
                                    hasPendingThinking = false;
                                }
                                // Rent a pooled StringBuilder for accumulating tool-call arg deltas
                                // (the previous per-call `new StringBuilder()` allocated on every
                                // tool-call start, which can be dozens per turn).
                                pendingToolCalls[tcs.Id] = (tcs.ToolName, StringBuilderPool.Rent());
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case ToolCallDeltaEvent tcd:
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.LogTrace("ToolCallDelta id={Id} argsDelta={Args}", tcd.Id, tcd.ArgsDelta);
                                }
                                if (pendingToolCalls.TryGetValue(tcd.Id, out var acc))
                                {
                                    acc.Args.Builder.Append(tcd.ArgsDelta);
                                    pendingToolCalls[tcd.Id] = acc;
                                }
                                await _eventBus.PublishAsync(new MessageUpdateEvent(evt, partial), ct).ConfigureAwait(false);
                                break;

                            case StepFinishEvent sf:
                                // Flush any pending text/thinking before finalizing.
                                if (hasPendingText)
                                {
                                    partial = partial.AppendText(textBuffer.ToString());
                                    textBuffer.Builder.Clear();
                                    hasPendingText = false;
                                }
                                if (hasPendingThinking)
                                {
                                    partial = partial.AppendThinking(thinkingBuffer.ToString());
                                    thinkingBuffer.Builder.Clear();
                                    hasPendingThinking = false;
                                }

                                // Materialize any tool calls accumulated from Start/Delta fragments.
                                foreach ((string id, (string name, var args)) in pendingToolCalls)
                                {
                                    JsonElement parsedArgs;
                                    try
                                    {
                                        // Use the pooled builder's contents directly. Avoids
                                        // allocating a fresh string when args is empty (the common
                                        // case for tools that take no arguments).
                                        string jsonText = args.Builder.Length == 0 ? "{}" : args.ToString();
                                        using var doc = JsonDocument.Parse(jsonText);
                                        parsedArgs = doc.RootElement.Clone();
                                    }
                                    catch (JsonException)
                                    {
                                        // Previously this line leaked the JsonDocument: it was
                                        // neither `using`-disposed nor explicitly disposed.
                                        using var fallback = JsonDocument.Parse("{}");
                                        parsedArgs = fallback.RootElement.Clone();
                                    }
                                    finally
                                    {
                                        // Return the per-tool-call pooled StringBuilder to the pool.
                                        args.Dispose();
                                    }

                                    // Intern the tool name via StringPool — tool names are highly repeated.
                                    string internedName = StringPool.Shared.GetOrAdd(name);
                                    var newToolCall = new ToolCallPart(id, internedName, parsedArgs);
                                    partial = partial.AppendToolCall(newToolCall);
                                    toolCalls.Add(newToolCall);
                                }
                                pendingToolCalls.Clear();

                                finalUsage = sf.Usage;
                                stopReason = StopReasonJsonConverter.Parse(sf.FinishReason);
                                partial = partial.WithFinish(stopReason, finalUsage ?? new Usage(0, 0));
                                break;

                            case ErrorEvent err:
                                // Return any per-tool-call pooled StringBuilders before early-returning.
                                foreach (var (_, entry) in pendingToolCalls)
                                {
                                    entry.Args.Dispose();
                                }
                                pendingToolCalls.Clear();
                                await _eventBus.PublishAsync(new AgentErrorEvent(err.Message, err.Exception), ct).ConfigureAwait(false);
                                return Result.Failure(err.Message);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Flush any pending buffers before recording the aborted finish.
                    if (hasPendingText)
                    {
                        partial = partial.AppendText(textBuffer.ToString());
                        textBuffer.Builder.Clear();
                    }
                    if (hasPendingThinking)
                    {
                        partial = partial.AppendThinking(thinkingBuffer.ToString());
                        thinkingBuffer.Builder.Clear();
                    }
                    // Return any per-tool-call pooled StringBuilders to the pool — otherwise
                    // cancellation mid-stream would leak them.
                    foreach (var (_, entry) in pendingToolCalls)
                    {
                        entry.Args.Dispose();
                    }
                    pendingToolCalls.Clear();
                    partial = partial.WithFinish(StopReason.Aborted, finalUsage ?? new Usage(0, 0));
                }

                _logger.LogDebug("Message end: turn={Turn} stopReason={StopReason}", turn, stopReason);
                await _eventBus.PublishAsync(new MessageEndEvent(partial), ct).ConfigureAwait(false);
                await session.AppendMessageAsync(partial, ct).ConfigureAwait(false);
                if (finalUsage != null)
                {
                    await session.UpdateStatsAsync(finalUsage, ct).ConfigureAwait(false);
                }

                // 7. No tool calls? done
                _logger.LogDebug("Turn {Turn}: toolCalls={ToolCalls} stopReason={StopReason}", turn, toolCalls.Count, stopReason);
                if (toolCalls.Count == 0 || stopReason is StopReason.Stop or StopReason.Length or StopReason.Aborted)
                {
                    _logger.LogDebug("Turn {Turn} end (no tool calls)", turn);
                    await _eventBus.PublishAsync(
                        new TurnEndEvent(partial, Array.Empty<ToolResultMessage>()), ct).ConfigureAwait(false);
                    break;
                }

                // 8. Execute tool calls
                var toolResults = await ExecuteToolCallsAsync(
                    toolCalls, session, partial, agent, ct).ConfigureAwait(false);

                // Persist the tool results so the next turn can feed them back
                // to the model (OpenAI requires a `tool` role message after a
                // tool_call, otherwise the model loops calling the same tool).
                await session.AppendMessageAsync(toolResults, ct).ConfigureAwait(false);

                _logger.LogDebug("Turn {Turn} end (with tool results)", turn);
                await _eventBus.PublishAsync(
                    new TurnEndEvent(partial, new[] { toolResults }), ct).ConfigureAwait(false);

                // 9. Steering check
                if (session.SteeringQueue.Reader.TryRead(out var steerMsg))
                {
                    await session.AppendMessageAsync(steerMsg, ct).ConfigureAwait(false);
                }

                // 10. Max steps
                if (turn >= agent.MaxSteps)
                {
                    _logger.LogInformation("Agent reached max steps ({MaxSteps})", agent.MaxSteps);
                    break;
                }
            }

            _logger.LogInformation("Agent loop completed: agent={Agent}", agent.Name.Value);
            await _eventBus.PublishAsync(
                new AgentEndEvent(SnapshotMessages(session.Messages)), ct).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent loop failed");
            await _eventBus.PublishAsync(new AgentErrorEvent(ex.Message, ex.ToString()), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(ex.Message);
        }
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

    private async Task<ToolResultMessage> ExecuteToolCallsAsync(
        IReadOnlyList<ToolCallPart> toolCalls,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct)
    {
        bool hasSequential = false;
        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            var toolNameResult = ToolName.TryCreate(tc.ToolName);
            if (toolNameResult.IsSuccess)
            {
                var toolResult = _tools.GetTool(toolNameResult.Value);
                if (toolResult.IsSuccess && toolResult.Value.ExecutionMode == ExecutionMode.Sequential)
                {
                    hasSequential = true;
                    break;
                }
            }
        }

        var results = new List<ToolResultEntry>(toolCalls.Count);

        if (hasSequential)
        {
            foreach (var tc in toolCalls)
            {
                var result = await ExecuteSingleToolCallAsync(tc, session, partial, agent, ct).ConfigureAwait(false);
                results.Add(result);
            }
        }
        else
        {
            // Rent the task array from the ArrayPool — Task.WhenAll accepts an IEnumerable<Task>,
            // so we can pass a Span-based slice without the secondary ToArray() allocation.
            // The pooled array is cleared before return so the Task references don't keep
            // the underlying async state machines alive longer than necessary.
            Task<ToolResultEntry>[]? tasks = null;
            try
            {
                tasks = ArrayPool<Task<ToolResultEntry>>.Shared.Rent(toolCalls.Count);
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    tasks[i] = ExecuteSingleToolCallAsync(toolCalls[i], session, partial, agent, ct);
                }

                // Task.WhenAll takes an IEnumerable; wrap the active slice in a minimal
                // struct enumerator so we don't materialize a second array via ToArray().
                var resolved = await Task.WhenAll(new ArraySegment<Task<ToolResultEntry>>(tasks, 0, toolCalls.Count)).ConfigureAwait(false);
                results.AddRange(resolved);
            }
            finally
            {
                if (tasks is not null)
                {
                    Array.Clear(tasks, 0, toolCalls.Count);
                    ArrayPool<Task<ToolResultEntry>>.Shared.Return(tasks);
                }
            }
        }

        return new ToolResultMessage(
            Guid.NewGuid().ToString("N"),
            session.Session.Id,
            DateTimeOffset.UtcNow,
            results);
    }

    private async Task<ToolResultEntry> ExecuteSingleToolCallAsync(
        ToolCallPart toolCall,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct)
    {
        var toolNameResult = ToolName.TryCreate(toolCall.ToolName);
        if (toolNameResult.IsFailure)
        {
            return new ToolResultEntry(
                toolCall.Id,
                toolCall.ToolName,
                $"Invalid tool name: {toolNameResult.Error}",
                true);
        }

        var toolResult = _tools.GetTool(toolNameResult.Value);
        if (toolResult.IsFailure)
        {
            // Build the "available tools" list with a pooled StringBuilder instead of
            // `.Select(...).JoinToString(...)` (which allocates an iterator + intermediate list).
            string available;
            using (var avail = StringBuilderPool.Rent(128))
            {
                var allTools = _tools.GetAllTools();
                for (int i = 0; i < allTools.Count; i++)
                {
                    if (avail.Builder.Length > 0) avail.Builder.Append(", ");
                    avail.Builder.Append(allTools[i].Name.Value);
                }
                available = avail.ToString();
            }
            return new ToolResultEntry(
                toolCall.Id,
                toolCall.ToolName,
                $"Unknown tool: '{toolCall.ToolName}'. Available: {available}",
                true);
        }

        await _eventBus.PublishAsync(new ToolExecutionStartEvent(
            toolCall.Id, toolCall.ToolName, toolCall.Args), ct).ConfigureAwait(false);
        _logger.LogDebug("Tool execution start: {ToolName} (call {CallId})", toolCall.ToolName, toolCall.Id);

        try
        {
            var tool = toolResult.Value;

            // Argument validation — returns a tool error instead of letting the
            // tool throw (e.g. KeyNotFoundException on a missing required prop).
            var validation = tool.ValidateArguments(toolCall.Args);
            if (validation.IsFailure)
            {
                var invalid = ToolResult.Error(validation.Error);
                await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                    toolCall.Id, invalid, true), ct).ConfigureAwait(false);
                return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, invalid);
            }

            // Permission check
            var permResponse = await _permissions.CheckAsync(
                agent.Name.Value, toolCall.ToolName, toolCall.Args, ct).ConfigureAwait(false);

            if (permResponse.IsSuccess && permResponse.Value.Action == PermissionAction.Deny)
            {
                var denied = ToolResult.Error("Permission denied");
                await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                    toolCall.Id, denied, true), ct).ConfigureAwait(false);
                return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, denied);
            }

            // Execute
            // Guard the GetRawText() call with IsEnabled — JsonElement.GetRawText()
            // allocates a fresh string every call, and LogDebug evaluates its args
            // eagerly before checking whether Debug is enabled. The guard eliminates
            // the per-tool-call string allocation when debug logging is off (the
            // common production case).
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Executing tool {ToolName} (call {CallId}) args={Args}", toolCall.ToolName, toolCall.Id, toolCall.Args.GetRawText());
            }
            var ctx = new ToolContext(
                session.Session.Id,
                partial.Id,
                toolCall.Id,
                agent.Name.Value,
                ct,
                session.Messages,
                (update, c) =>
                {
                    _ = _eventBus.PublishAsync(new ToolExecutionUpdateEvent(toolCall.Id, update.PartialResult ?? update), c);
                    return Task.CompletedTask;
                },
                // async/await instead of ContinueWith + .Result: the latter allocates a
                // continuation Task and accesses .Result which (though safe here because
                // the antecedent is already complete) is a foot-gun. The async state
                // machine is slightly cheaper and clearer about intent.
                async (req, c) => (await _permissions.AskUserAsync(req, c).ConfigureAwait(false)).Value,
                null!);

            var result = await tool.ExecuteAsync(toolCall.Args, ctx, ct).ConfigureAwait(false);

            _logger.LogDebug("Tool execution end: {ToolName} (call {CallId}) isError={IsError}", toolCall.ToolName, toolCall.Id, result.IsError);
            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, result, result.IsError), ct).ConfigureAwait(false);

            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelled = ToolResult.Error("Tool execution was cancelled.");
            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, cancelled, true), ct).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} failed", toolCall.ToolName);
            var errored = ToolResult.Error($"Tool execution failed: {ex.Message}");
            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, errored, true), ct).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, errored);
        }
    }
}
