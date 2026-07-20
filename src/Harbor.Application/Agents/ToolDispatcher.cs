using System.Buffers;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Core.Agents;

/// <summary>
///     Dispatches tool calls to the registered <see cref="ITool"/>s and
///     aggregates results into a <see cref="ToolResultMessage"/>. Extracted
///     from <see cref="AgentLoop"/> (Task R32 god-object decomposition) so
///     the loop can focus on orchestration while this class owns tool
///     execution, permission gating, and event publishing.
/// </summary>
/// <remarks>
///     <para>
///         <b>Execution modes:</b> if any tool in the batch declares
///         <see cref="ExecutionMode.Sequential"/> (e.g. <c>bash</c>,
///         <c>write</c>), the entire batch runs sequentially. Otherwise
///         the batch runs in parallel via <see cref="Task.WhenAll"/> with
///         an ArrayPool-rented task array (avoids the LINQ
///         <c>Select(...).ToArray()</c> allocation).
///     </para>
///     <para>
///         <b>Per-tool-call lifecycle:</b>
///         <list type="number">
///             <item>Validate tool name + look up the tool via <see cref="IToolRegistry"/>.</item>
///             <item>Validate arguments via <see cref="ITool.ValidateArguments"/>.</item>
///             <item>Check permission via <see cref="IPermissionService.CheckAsync"/>.</item>
///             <item>Publish <see cref="ToolExecutionStartEvent"/>.</item>
///             <item>Execute via <see cref="ITool.ExecuteAsync"/> with a <see cref="ToolContext"/> that wires up progress reporting + user-prompt callback.</item>
///             <item>Publish <see cref="ToolExecutionEndEvent"/> (success or error).</item>
///         </list>
///     </para>
///     <para>
///         <b>Error handling:</b> validation errors, permission denies, and
///         exceptions are all converted into <see cref="ToolResultEntry"/>
///         with <c>IsError=true</c> — the agent loop treats them as
///         successful "tool returned an error" rather than throwing.
///     </para>
/// </remarks>
internal sealed class ToolDispatcher
{
    private readonly IToolRegistry _tools;
    private readonly IPermissionService _permissions;
    private readonly IEventBus _eventBus;
#pragma warning disable S6672 // Logger category should match enclosing type — ToolDispatcher is internal, sharing AgentLoop's logger is fine
    private readonly ILogger<AgentLoop> _logger;
#pragma warning restore S6672

    public ToolDispatcher(
        IToolRegistry tools,
        IPermissionService permissions,
        IEventBus eventBus,
#pragma warning disable S6672
        ILogger<AgentLoop> logger)
#pragma warning restore S6672
    {
        _tools = tools;
        _permissions = permissions;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    ///     Execute a batch of tool calls either sequentially (if any tool
    ///     declares <see cref="ExecutionMode.Sequential"/>) or in parallel.
    ///     Returns a <see cref="ToolResultMessage"/> ready to append to
    ///     the session.
    /// </summary>
    public async Task<ToolResultMessage> ExecuteAsync(
        IReadOnlyList<ToolCallPart> toolCalls,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct)
    {
        bool hasSequential = HasSequentialTool(toolCalls);

        var results = new List<ToolResultEntry>(toolCalls.Count);

        if (hasSequential)
        {
            foreach (var tc in toolCalls)
            {
                var result = await ExecuteSingleAsync(tc, session, partial, agent, ct).ConfigureAwait(false);
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
                    tasks[i] = ExecuteSingleAsync(toolCalls[i], session, partial, agent, ct);
                }

                var resolved = await Task.WhenAll(
                    new ArraySegment<Task<ToolResultEntry>>(tasks, 0, toolCalls.Count)).ConfigureAwait(false);
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

    /// <summary>
    ///     Check if any tool in the batch declares <see cref="ExecutionMode.Sequential"/>.
    ///     If so, the entire batch must run sequentially (otherwise sequential
    ///     tools would race on shared state like the file system or shell).
    /// </summary>
    private bool HasSequentialTool(IReadOnlyList<ToolCallPart> toolCalls)
    {
        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            var toolNameResult = ToolName.TryCreate(tc.ToolName);
            if (toolNameResult.IsSuccess)
            {
                var toolResult = _tools.GetTool(toolNameResult.Value);
                if (toolResult.IsSuccess && toolResult.Value.ExecutionMode == ExecutionMode.Sequential)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    ///     Execute a single tool call: validate name → validate args → check
    ///     permission → publish start event → execute → publish end event.
    ///     All error paths return a <see cref="ToolResultEntry"/> with
    ///     <c>IsError=true</c> rather than throwing.
    /// </summary>
    private async Task<ToolResultEntry> ExecuteSingleAsync(
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
                async (update, c) =>
                {
                    // §FP-003 (RESOLVED): previously `_ = _eventBus.PublishAsync(...)`
                    // was fire-and-forget — exceptions died as unobserved task exceptions
                    // and tool progress updates were silently dropped on bus back-pressure.
                    // The lambda is now async and awaits the publish with a try/catch so
                    // failures are logged without breaking tool execution. Return type is
                    // still `Task` per the ToolContext.ReportProgress contract.
                    try
                    {
                        await _eventBus.PublishAsync(new ToolExecutionUpdateEvent(toolCall.Id, update.PartialResult ?? update), c)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Tool progress publish failed for {ToolCallId}", toolCall.Id);
                    }
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
