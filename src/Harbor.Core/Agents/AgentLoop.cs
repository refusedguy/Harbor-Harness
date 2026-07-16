using System.Buffers;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;

namespace Harbor.Core.Agents;

/// <summary>
/// Default agent loop. Implements Chain of Responsibility pattern (GOF):
/// prompt → LLM stream → tool execution → next turn → (compaction if needed) → repeat.
///
/// Performance:
///  - Streaming deltas are coalesced in a pooled StringBuilder before being attached to the
///    partial message, reducing per-delta array allocations from O(n²) to O(n) per text run.
///  - Tool-definition arrays are sized directly instead of via LINQ Select().ToList().
///  - Tool names are interned via <see cref="StringPool"/> to deduplicate provider/tool strings.
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly IProviderRegistry _providers;
    private readonly IToolRegistry _tools;
    private readonly IAgentRegistry _agents;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly ICompactionService _compaction;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly IEventBus _eventBus;
    private readonly IPermissionService _permissions;
    private readonly MessageConverter _messageConverter;
    private readonly ILogger<AgentLoop> _logger;

    /// <summary>
    /// Construct an <see cref="AgentLoop"/> wired to the supplied services.
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
    /// Run the agent loop to completion: prompt → LLM stream → tool execution → next turn,
    /// repeating until either no tool calls are emitted or <see cref="AgentDefinition.MaxSteps"/>
    /// is reached. Compaction runs at the start of each turn if the token estimator says so.
    /// </summary>
    /// <param name="session">The session context for this run.</param>
    /// <param name="agent">The agent definition driving the loop.</param>
    /// <param name="ct">Cancellation token used to abort the run at the next safe boundary.</param>
    /// <returns>Success on normal completion, or failure with an error message.</returns>
    public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
    {
        try
        {
            await _eventBus.PublishAsync(new AgentStartEvent(session.Session.Id, SnapshotMessages(session.Messages)), ct).ConfigureAwait(false);

            var turn = 0;
            while (!ct.IsCancellationRequested)
            {
                turn++;
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
                    await _eventBus.PublishAsync(new CompactionStartedEvent(session.Session.Id), ct).ConfigureAwait(false);
                    var compactionResult = await _compaction.CompactAsync(session.Session.Id, session.Messages, model, ct).ConfigureAwait(false);

                    // Railway Oriented Programming: Match dispatches to the
                    // success or failure branch without an explicit
                    // `if (result.IsSuccess)` check, making the
                    // happy-path/error-path split structural rather than
                    // control-flow.
                    await compactionResult.Match(
                        onSuccess: async result =>
                        {
                            await session.AppendMessageAsync(result.SummaryMessage, ct).ConfigureAwait(false);
                            await _eventBus.PublishAsync(new CompactionCompletedEvent(
                                session.Session.Id,
                                result.Summary,
                                result.PrunedMessageCount,
                                result.TokensSaved,
                                result.Duration), ct).ConfigureAwait(false);
                        },
                        onFailure: error =>
                        {
                            _logger.LogWarning("Compaction failed: {Error}", error);
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                }

                // 3. Build system prompt
                var tools = _tools.ResolveTools(agent.Name.Value, agent.Permission);
                var promptContext = new SystemPromptContext(
                    Agent: agent,
                    Model: model,
                    Tools: tools,
                    ContextFiles: Array.Empty<ContextFile>(),
                    Skills: Array.Empty<SkillDescriptor>(),
                    McpInstructions: null,
                    WorkingDirectory: session.Session.Directory);
                var systemPrompt = await _promptBuilder.BuildAsync(promptContext, ct).ConfigureAwait(false);

                // 4. Convert messages
                var llmMessages = _messageConverter.ToLlmMessages(session.Messages);

                // 5. Build request — size the ToolDefinition array directly instead of LINQ Select().ToList().
                var toolDefs = BuildToolDefinitions(tools);
                var request = new LlmRequest(
                    Model: agent.Model,
                    Messages: llmMessages,
                    SystemPrompt: systemPrompt,
                    Tools: toolDefs,
                    MaxOutputTokens: model.MaxOutputTokens,
                    Temperature: agent.Temperature,
                    ReasoningEffort: agent.ReasoningEffort);

                // 6. Stream LLM — coalesce consecutive text/thinking deltas in pooled StringBuilders
                //    to avoid creating a new array per delta (the previous AppendText per-delta approach
                //    was O(n²) in array allocations).
                var partial = AssistantMessage.Empty(session.Session.Id, model.Id);
                await _eventBus.PublishAsync(new MessageStartEvent(partial), ct).ConfigureAwait(false);

                // Pre-size to typical tool-call count to avoid List resizes.
                var toolCalls = new List<ToolCallPart>(capacity: 4);
                Usage? finalUsage = null;
                var stopReason = StopReason.Stop;

                using var textBuffer = StringBuilderPool.Rent(4096);
                using var thinkingBuffer = StringBuilderPool.Rent(1024);
                var hasPendingText = false;
                var hasPendingThinking = false;

                try
                {
                    await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
                    {
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

                            case ToolCallEndEvent tce:
                                // Flush any pending text/thinking before attaching the tool call.
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
                                // Intern the tool name via StringPool — tool names are highly repeated.
                                var internedName = StringPool.Shared.GetOrAdd(tce.ToolName);
                                var newToolCall = new ToolCallPart(tce.Id, internedName, tce.Args);
                                partial = partial.AppendToolCall(newToolCall);
                                toolCalls.Add(newToolCall);
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
                                finalUsage = sf.Usage;
                                if (Enum.TryParse<StopReason>(sf.FinishReason, ignoreCase: true, out var sr))
                                {
                                    stopReason = sr;
                                }
                                partial = partial.WithFinish(stopReason, finalUsage ?? new Usage(0, 0));
                                break;

                            case ErrorEvent err:
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
                    partial = partial.WithFinish(StopReason.Aborted, finalUsage ?? new Usage(0, 0));
                }

                await _eventBus.PublishAsync(new MessageEndEvent(partial), ct).ConfigureAwait(false);
                await session.AppendMessageAsync(partial, ct).ConfigureAwait(false);
                if (finalUsage != null)
                {
                    await session.UpdateStatsAsync(finalUsage, ct).ConfigureAwait(false);
                }

                // 7. No tool calls? done
                if (toolCalls.Count == 0 || stopReason is StopReason.Stop or StopReason.Length or StopReason.Aborted)
                {
                    await _eventBus.PublishAsync(
                        new TurnEndEvent(partial, Array.Empty<ToolResultMessage>()), ct).ConfigureAwait(false);
                    break;
                }

                // 8. Execute tool calls
                var toolResults = await ExecuteToolCallsAsync(
                    toolCalls, session, partial, agent, ct).ConfigureAwait(false);

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
    /// Build the ToolDefinition array directly, avoiding the LINQ Select().ToList() allocation
    /// (which allocates a delegate + iterator + List).
    /// </summary>
    private static ToolDefinition[] BuildToolDefinitions(IReadOnlyList<ToolDescriptor> tools)
    {
        if (tools.Count == 0)
        {
            return Array.Empty<ToolDefinition>();
        }

        var result = new ToolDefinition[tools.Count];
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            result[i] = new ToolDefinition(t.Name.Value, t.Description, t.Schema);
        }
        return result;
    }

    /// <summary>
    /// Linear scan for the requested model — avoids LINQ FirstOrDefault delegate allocation.
    /// </summary>
    private static ModelInfo? FindModel(IReadOnlyList<ModelInfo> models, string modelId)
    {
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i].Id == modelId)
            {
                return models[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Materialize a snapshot list of the current session messages for events.
    /// </summary>
    private static List<AgentMessage> SnapshotMessages(IReadOnlyList<AgentMessage> messages)
    {
        var snapshot = new List<AgentMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
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
        var hasSequential = false;
        for (var i = 0; i < toolCalls.Count; i++)
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
            // Size the task array directly (no LINQ Select).
            var tasks = new Task<ToolResultEntry>[toolCalls.Count];
            for (var i = 0; i < toolCalls.Count; i++)
            {
                tasks[i] = ExecuteSingleToolCallAsync(toolCalls[i], session, partial, agent, ct);
            }
            var resolved = await Task.WhenAll(tasks).ConfigureAwait(false);
            results.AddRange(resolved);
        }

        return new ToolResultMessage(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: session.Session.Id,
            CreatedAt: DateTimeOffset.UtcNow,
            Results: results);
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
                IsError: true);
        }

        var toolResult = _tools.GetTool(toolNameResult.Value);
        if (toolResult.IsFailure)
        {
            var available = _tools.GetAllTools().Select(t => t.Name.Value).JoinToString(", ");
            return new ToolResultEntry(
                toolCall.Id,
                toolCall.ToolName,
                $"Unknown tool: '{toolCall.ToolName}'. Available: {available}",
                IsError: true);
        }

        await _eventBus.PublishAsync(new ToolExecutionStartEvent(
            toolCall.Id, toolCall.ToolName, toolCall.Args), ct).ConfigureAwait(false);

        try
        {
            var tool = toolResult.Value;

            // Permission check
            var permResponse = await _permissions.CheckAsync(
                agent.Name.Value, toolCall.ToolName, toolCall.Args, ct).ConfigureAwait(false);

            if (permResponse.IsSuccess && permResponse.Value.Action == PermissionAction.Deny)
            {
                var denied = ToolResult.Error($"Permission denied");
                await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                    toolCall.Id, denied, IsError: true), ct).ConfigureAwait(false);
                return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, denied);
            }

            // Execute
            var ctx = new ToolContext(
                SessionId: session.Session.Id,
                MessageId: partial.Id,
                CallId: toolCall.Id,
                Agent: agent.Name.Value,
                Abort: ct,
                Messages: session.Messages,
                ReportProgress: (update, c) =>
                {
                    _ = _eventBus.PublishAsync(new ToolExecutionUpdateEvent(toolCall.Id, update.PartialResult ?? update), c);
                    return Task.CompletedTask;
                },
                Ask: (req, c) => _permissions.AskUserAsync(req, c).ContinueWith(t => t.Result.Value, c),
                Services: null!);

            var result = await tool.ExecuteAsync(toolCall.Args, ctx, ct).ConfigureAwait(false);

            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, result, result.IsError), ct).ConfigureAwait(false);

            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelled = ToolResult.Error("Tool execution was cancelled.");
            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, cancelled, IsError: true), ct).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} failed", toolCall.ToolName);
            var errored = ToolResult.Error($"Tool execution failed: {ex.Message}");
            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, errored, IsError: true), ct).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, errored);
        }
    }
}
