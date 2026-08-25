using System.Buffers;
using System.Diagnostics;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Agents;
/// <summary>
///     Dispatches tool calls to the registered <see cref="ITool" />s and
///     aggregates results into a <see cref="ToolResultMessage" />. Extracted
///     from <see cref="AgentLoop" /> (Task R32 god-object decomposition) so
///     the loop can focus on orchestration while this class owns tool
///     execution, permission gating, and event publishing.
/// </summary>
/// <remarks>
///     <para>
///         <b>Execution modes:</b> if any tool in the batch declares
///         <see cref="ExecutionMode.Sequential" /> (e.g. <c>bash</c>,
///         <c>write</c>), the entire batch runs sequentially. Otherwise
///         the batch runs in parallel via <see cref="Task.WhenAll" /> with
///         an ArrayPool-rented task array (avoids the LINQ
///         <c>Select(...).ToArray()</c> allocation).
///     </para>
///     <para>
///         <b>Per-tool-call lifecycle:</b>
///         <list type="number">
///             <item>Validate tool name + look up the tool via <see cref="IToolRegistry" />.</item>
///             <item>Validate arguments via <see cref="ITool.ValidateArguments" />.</item>
///             <item>Check permission via <see cref="IPermissionService.CheckAsync" />.</item>
///             <item>Publish <see cref="ToolExecutionStartEvent" />.</item>
///             <item>
///                 Execute via <see cref="ITool.ExecuteAsync" /> with a <see cref="ToolContext" /> that wires up
///                 progress reporting + user-prompt callback.
///             </item>
///             <item>Publish <see cref="ToolExecutionEndEvent" /> (success or error).</item>
///         </list>
///     </para>
///     <para>
///         <b>Error handling:</b> validation errors, permission denies, and
///         exceptions are all converted into <see cref="ToolResultEntry" />
///         with <c>IsError=true</c> — the agent loop treats them as
///         successful "tool returned an error" rather than throwing.
///     </para>
/// </remarks>
/// <remarks>
///     <b>ROP-C П.5:</b> public so hosts can construct the default dispatcher
///     when wiring <see cref="IToolDispatcher" /> in DI.
/// </remarks>
public sealed class ToolDispatcher(
    IToolRegistry tools,
    IPermissionService permissions,
    IEventBus eventBus,
    // ROP-C П.8: own category instead of the borrowed ILogger<AgentLoop>
    // (S6672) — dispatcher records are filterable by their own type.
    ILogger<ToolDispatcher> logger) : IToolDispatcher
{
    private static readonly ActivitySource Source = new("Harbor");
    private const string ToolNameTag = "gen_ai.tool.name";

    /// <summary>
    ///     Execute a batch of tool calls either sequentially (if any tool
    ///     declares <see cref="ExecutionMode.Sequential" />) or in parallel.
    ///     Returns a <see cref="ToolResultMessage" /> ready to append to
    ///     the session.
    /// </summary>
    public async Task<ToolResultMessage> ExecuteAsync(
        IReadOnlyList<ToolCallPart> toolCalls,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout = null)
    {
        bool hasSequential = HasSequentialTool(toolCalls);

        var results = new List<ToolResultEntry>(toolCalls.Count);

        if (hasSequential)
        {
            foreach (var tc in toolCalls)
            {
                var result = await ExecuteSingleAsync(tc, session, partial, agent, ct, toolExecutionTimeout).ConfigureAwait(false);
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
                    tasks[i] = ExecuteSingleAsync(toolCalls[i], session, partial, agent, ct, toolExecutionTimeout);
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
    ///     Check if any tool in the batch declares <see cref="ExecutionMode.Sequential" />.
    ///     If so, the entire batch must run sequentially (otherwise sequential
    ///     tools would race on shared state like the file system or shell).
    /// </summary>
    /// <remarks>
    ///     ROP-B П.19: the 4-level nested IsSuccess ladder collapses to a single
    ///     combinator predicate per call. Unresolvable entries fold to
    ///     <see langword="false" /> here — their proper error entries are still
    ///     produced per-call by <see cref="ExecuteSingleAsync" />.
    /// </remarks>
    private bool HasSequentialTool(IReadOnlyList<ToolCallPart> toolCalls)
    {
        for (int i = 0; i < toolCalls.Count; i++)
        {
            bool sequential = ToolName.TryCreate(toolCalls[i].ToolName)
                .Bind(tools.GetTool)
                .Map(static t => t.ExecutionMode == ExecutionMode.Sequential)
                .Match(static v => v, static _ => false);
            if (sequential)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Parse a raw tool name and resolve it to a registered tool on one
    ///     railway. Failure text is produced at the source of each failure:
    ///     invalid format via MapError, unknown tool (with the available-tools
    ///     inventory) via the registry-miss branch.
    /// </summary>
    private Result<ITool> ResolveTool(string rawName) =>
        ToolName.TryCreate(rawName)
            .MapError(e => $"Invalid tool name: {e}")
            .Bind(name => tools.GetTool(name).MapError(_ => UnknownToolDiagnostic(rawName)));

    /// <summary>
    ///     Build the "available tools" list with a pooled StringBuilder instead of
    ///     `.Select(...).JoinToString(...)` (which allocates an iterator + intermediate list).
    /// </summary>
    private string UnknownToolDiagnostic(string rawName)
    {
        using var avail = StringBuilderPool.Rent(128);
        var allTools = tools.GetAllTools();
        for (int i = 0; i < allTools.Count; i++)
        {
            if (avail.Builder.Length > 0) avail.Builder.Append(", ");
            avail.Builder.Append(allTools[i].Name.Value);
        }

        return $"Unknown tool: '{rawName}'. Available: {avail}";
    }

    /// <summary>
    ///     Execute a single tool call: validate name → validate args → check
    ///     permission → publish start event → execute → publish end event.
    ///     All error paths return a <see cref="ToolResultEntry" /> with
    ///     <c>IsError=true</c> rather than throwing.
    /// </summary>
    private async Task<ToolResultEntry> ExecuteSingleAsync(
        ToolCallPart toolCall,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout = null)
    {
        using var activity = Source.StartActivity("Tool.Execute");
        activity?.SetTag(ToolNameTag, toolCall.ToolName);

        // ROP-C П.4: the two guard ladders (name parse → registry lookup) ride
        // one Bind railway. Diagnostics stay distinct by construction: MapError
        // localizes "invalid name" at its source and the registry-miss branch
        // carries the available-tools inventory (rop-final-mile L5 boundary).
        Result<ITool> resolved = ResolveTool(toolCall.ToolName);
        if (resolved.IsFailure)
        {
            return new ToolResultEntry(toolCall.Id, toolCall.ToolName, resolved.Error, true);
        }

        ITool tool = resolved.Value;

        await eventBus.PublishAsync(new ToolExecutionStartEvent(
            toolCall.Id, toolCall.ToolName, toolCall.Args), ct).ConfigureAwait(false);
        logger.LogDebug("Tool execution start: {ToolName} (call {CallId})", toolCall.ToolName, toolCall.Id);

        // A9: arm the per-call deadline (if configured). The linked token is
        // passed to permission check AND execution so a hanging tool's awaits
        // observe the cancel and the dispatcher can synthesize an error entry.
        CancellationTokenSource? timeoutCts = null;
        if (toolExecutionTimeout is { } deadline)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(deadline);
        }

        using (timeoutCts)
        {
            CancellationToken effectiveCt = timeoutCts?.Token ?? ct;
            try
            {
            // Argument validation — returns a tool error instead of letting the
            // tool throw (e.g. KeyNotFoundException on a missing required prop).
            var validation = tool.ValidateArguments(toolCall.Args);
            if (validation.IsFailure)
            {
                activity?.SetStatus(ActivityStatusCode.Error, validation.Error);
                var invalid = ToolResult.Error(validation.Error);
                await eventBus.PublishAsync(new ToolExecutionEndEvent(
                    toolCall.Id, invalid, true), ct).ConfigureAwait(false);
                return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, invalid);
            }

            // Permission check
            var permResponse = await permissions.CheckAsync(
                agent.Name.Value, toolCall.ToolName, toolCall.Args, effectiveCt).ConfigureAwait(false);

            // G3 fail-closed: a permission-SUBSYSTEM failure (agent not in the
            // registry, invalid name) used to fall through to execution — i.e.
            // every tool ran as "allow all". Any non-success verdict now denies.
            if (permResponse.IsFailure || permResponse.Value.Action == PermissionAction.Deny)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Permission denied");
                string reason = permResponse.IsFailure
                    ? $"Permission check failed: {permResponse.Error}"
                    : "Permission denied";
                var denied = ToolResult.Error(reason);
                await eventBus.PublishAsync(new ToolExecutionEndEvent(
                    toolCall.Id, denied, true), ct).ConfigureAwait(false);
                return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, denied);
            }

            // Execute
            // Guard the GetRawText() call with IsEnabled — JsonElement.GetRawText()
            // allocates a fresh string every call, and LogDebug evaluates its args
            // eagerly before checking whether Debug is enabled. The guard eliminates
            // the per-tool-call string allocation when debug logging is off (the
            // common production case).
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Executing tool {ToolName} (call {CallId}) args={Args}", toolCall.ToolName, toolCall.Id, toolCall.Args.GetRawText());
            }
            var ctx = new ToolContext(
                session.Session.Id,
                partial.Id,
                toolCall.Id,
                agent.Name.Value,
                effectiveCt,
                session.Messages,
                async (update, c) =>
                {
                    // §FP-003 (RESOLVED): previously `_ = eventBus.PublishAsync(...)`
                    // was fire-and-forget — exceptions died as unobserved task exceptions
                    // and tool progress updates were silently dropped on bus back-pressure.
                    // The lambda is now async and awaits the publish with a try/catch so
                    // failures are logged without breaking tool execution. Return type is
                    // still `Task` per the ToolContext.ReportProgress contract.
                    try
                    {
                        await eventBus.PublishAsync(new ToolExecutionUpdateEvent(toolCall.Id, update.PartialResult ?? update), c)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Tool progress publish failed for {ToolCallId}", toolCall.Id);
                    }
                },
                // async/await instead of ContinueWith + .Result: the latter allocates a
                // continuation Task and accesses .Result which (though safe here because
                // the antecedent is already complete) is a foot-gun. The async state
                // machine is slightly cheaper and clearer about intent.
                async (req, c) => (await permissions.AskUserAsync(req, c).ConfigureAwait(false)).Value,
                null!);

            var result = await tool.ExecuteAsync(toolCall.Args, ctx, effectiveCt).ConfigureAwait(false);

            logger.LogDebug("Tool execution end: {ToolName} (call {CallId}) isError={IsError}", toolCall.ToolName, toolCall.Id, result.IsError);
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, result, result.IsError), effectiveCt).ConfigureAwait(false);

            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelled = ToolResult.Error("Tool execution was cancelled.");
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, cancelled, true), ct).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, cancelled);
        }
        catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
        {
            // A9: the per-call deadline fired (outer token NOT cancelled) —
            // synthesize an error entry so the loop keeps going.
            activity?.SetStatus(ActivityStatusCode.Error, "tool timed out");
            string message = toolExecutionTimeout is { } t
                ? $"Tool '{toolCall.ToolName}' timed out after {t.TotalSeconds:0.#}s."
                : "Tool execution was cancelled.";
            logger.LogWarning(oce, "Tool {ToolName} (call {CallId}) hit its execution deadline", toolCall.ToolName, toolCall.Id);
            var timeout = ToolResult.Error(message);
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, timeout, true), ct).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, timeout);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "Tool {ToolName} failed", toolCall.ToolName);
            var errored = ToolResult.Error($"Tool execution failed: {ex.Message}");
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, errored, true), effectiveCt).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, errored);
        }
        }
    }
}
