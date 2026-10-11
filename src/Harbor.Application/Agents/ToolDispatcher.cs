using System.Buffers;
using System.Diagnostics;
using Harbor.Abstractions.Extensions;
using Harbor.Application.Hooks;
using Harbor.Application.Resilience;
using Microsoft.Extensions.Logging;
namespace Harbor.Application.Agents;
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
///         <b>Stop boundary (#401, B2 core):</b> both dispatch loops test
///         <c>ct.IsCancellationRequested</c> before starting each call, so once
///         a stop is observed no further call in the batch is started. Calls
///         not yet started become <c>NotStartedBecauseStopped</c> entries.
///         Calls already running are awaited up to a bounded grace period
///         (<see cref="AbandonGraceDefault" />): a call that finishes after
///         the boundary is marked <b>Abandoned</b> rather than reported as
///         success, and a call still running when the grace expires is
///         abandoned the same way — its late fault is observed and logged,
///         never unobserved. This is the "accepted cancellation holds"
///         boundary — it observes the existing token, it does not introduce
///         a second stop state machine.
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
    ILogger<ToolDispatcher> logger,
    // #49 PR2: execution-commit barrier. Null (tests, manual construction)
    // keeps the legacy token-only path.
    IApprovalCoordinator? coordinator = null,
    // #43: retry decider (decision only; backoff via RetryPolicy.ComputeDelay).
    // Null keeps legacy no-retry behavior for direct constructions.
    IToolRetryDecider? retryDecider = null,
    // #401 B2: bounded grace for in-flight calls after the Accepted boundary.
    // Null keeps the default below; tests inject milliseconds.
    TimeSpan? abandonGrace = null,
    // #401 B2: clock for the grace wait. Null means the system clock, while
    // tests inject a fake when they need the boundary without wall-clock.
    TimeProvider? clock = null,
    // PX4: user hooks (PreToolUse/PostToolUse). Null (tests, manual
    // construction, fallback loops) keeps the legacy hooks-free path.
    IHookRunner? hookRunner = null) : IToolDispatcher
{
    private static readonly ActivitySource Source = new("Harbor");
    private const string ToolNameTag = "gen_ai.tool.name";

    /// <summary>
    ///     Named reason for the Accepted boundary (#401 B2): the run token
    ///     fired, so the stop is accepted and no new work may start. Rides
    ///     every Abandoned entry's text, so the cause is readable from the
    ///     persisted result without joining another table.
    /// </summary>
    public const string AcceptedStopReason = "accepted-stop";

    /// <summary>
    ///     Default grace period an in-flight call gets after the Accepted
    ///     boundary before it is reported Abandoned (#401 B2). Long enough for
    ///     a cooperative tool to unwind, short enough that a stuck call cannot
    ///     hold the run hostage.
    /// </summary>
    public static readonly TimeSpan AbandonGraceDefault = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _abandonGrace = abandonGrace ?? AbandonGraceDefault;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IHookRunner? _hooks = hookRunner;

    /// <summary>
    ///     Publish token for the TERMINAL event of a tool call (#401).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The end event is how a renderer learns a card stopped spinning.
    ///         Publishing it with the run token means a call that ended
    ///         <i>because</i> the run was cancelled publishes it with an
    ///         already-cancelled token — and a cancelled token is exactly the
    ///         case where the bus stops awaiting the subscriber (see
    ///         <c>InMemoryEventBus.DispatchToOneAsync</c>: an already-fired
    ///         <c>Task.Delay(Timeout.InfiniteTimeSpan, ct)</c> wins the
    ///         <c>WhenAny</c>, so the handler is left running unobserved). The
    ///         terminal record of a cancelled call is the one event that must
    ///         not depend on the cancellation it is reporting.
    ///     </para>
    ///     <para>
    ///         Matches what <c>AgentLoop</c> already does for
    ///         <c>AgentEndEvent(Cancelled: true)</c> and
    ///         <c>CompactionBehavior</c> for <c>CompactionFailedEvent</c>.
    ///     </para>
    /// </remarks>
    private static readonly CancellationToken TerminalEventToken = CancellationToken.None;

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
                // #401: an accepted stop ends NEW work. A sequential batch is
                // the shape where the gap is widest — call k finishing is the
                // natural moment for the user to press Stop, and without this
                // gate the very next iteration would start anyway.
                if (ct.IsCancellationRequested)
                {
                    results.Add(NotStartedBecauseStopped(tc));
                    continue;
                }

                // #401 B2: the in-flight side of the same boundary — a
                // sequential call that outlives the stop gets the grace, then
                // Abandoned, instead of holding the run open indefinitely.
                Task<ToolResultEntry> pending = ExecuteSingleAsync(tc, session, partial, agent, ct, toolExecutionTimeout);
                results.Add(await AwaitOneWithAbandonAsync(pending, tc, ct).ConfigureAwait(false));
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
                    // #401: the dispatch loop is the acceptance boundary. Each
                    // iteration is a fresh `ExecuteSingleAsync` invocation, so a
                    // cancel landing between iterations would otherwise start
                    // calls the user asked to stop. Dispatch is cheap and
                    // synchronous up to the permission gate, so the window is
                    // real (a permission-gate cancel from iteration k-1 lands
                    // inside this loop) rather than theoretical.
                    tasks[i] = ct.IsCancellationRequested
                        ? Task.FromResult(NotStartedBecauseStopped(toolCalls[i]))
                        : ExecuteSingleAsync(toolCalls[i], session, partial, agent, ct, toolExecutionTimeout);
                }

                var resolved = await AwaitAllWithAbandonAsync(
                    tasks, toolCalls, toolCalls.Count, ct).ConfigureAwait(false);
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
    ///     Result entry for a call that was never dispatched because a stop was
    ///     already observed (#401). Reports an error, exactly like every other
    ///     refusal path in this class, so the provider still sees one
    ///     <c>tool_result</c> per <c>tool_call</c> — a missing result breaks the
    ///     wire contract for OpenAI-compatible providers.
    /// </summary>
    /// <remarks>
    ///     Deliberately publishes <b>no</b> <see cref="ToolExecutionStartEvent" />:
    ///     nothing started, so there is no start to report and no end to pair it
    ///     with. The entry text is the honest record of the outcome.
    /// </remarks>
    private ToolResultEntry NotStartedBecauseStopped(ToolCallPart toolCall)
    {
        logger.LogInformation(
            "Tool {ToolName} (call {CallId}) not started — a stop was accepted before dispatch",
            toolCall.ToolName, toolCall.Id);
        return ToolResultEntry.From(
            toolCall.Id,
            toolCall.ToolName,
            ToolResult.Error("Tool execution was cancelled before start."));
    }

    /// <summary>
    ///     Wait for a dispatch to finish, but stop waiting when the run token
    ///     fires (#401 B2). A completed task returns immediately; otherwise the
    ///     wait ends at the Accepted boundary and the caller bounds the rest by
    ///     the grace period. Never throws for cancellation — the boundary is a
    ///     fact to handle, not an error to propagate.
    /// </summary>
    private static async Task WaitForCompletionOrAcceptAsync(Task completed, CancellationToken ct)
    {
        if (completed.IsCompleted)
        {
            return;
        }

        try
        {
            await completed.WaitAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stop was accepted while awaiting: the caller applies grace.
        }
    }

    /// <summary>
    ///     Accepted-boundary wait for one in-flight call (#401 B2). Without a
    ///     stop this is today's plain await; after the boundary the call gets
    ///     the grace period, then Abandoned.
    /// </summary>
    private async Task<ToolResultEntry> AwaitOneWithAbandonAsync(
        Task<ToolResultEntry> pending,
        ToolCallPart toolCall,
        CancellationToken ct)
    {
        await WaitForCompletionOrAcceptAsync(pending, ct).ConfigureAwait(false);

        if (!ct.IsCancellationRequested)
        {
            return await pending.ConfigureAwait(false);
        }

        if (pending.IsCompletedSuccessfully)
        {
            // Finished around the boundary: any post-Accepted success was
            // already converted to Abandoned inside ExecuteWithRetryAsync.
            return pending.Result;
        }

        logger.LogInformation(
            "Stop accepted ({Reason}); awaiting in-flight tool call {CallId} for {GraceMs:0}ms before abandoning",
            AcceptedStopReason, toolCall.Id, _abandonGrace.TotalMilliseconds);
        await Task.WhenAny(pending, Task.Delay(_abandonGrace, _clock)).ConfigureAwait(false);

        return pending.IsCompletedSuccessfully
            ? pending.Result
            : await AbandonLeftoverAsync(pending, toolCall).ConfigureAwait(false);
    }

    /// <summary>
    ///     Accepted-boundary wait for a parallel batch (#401 B2). Without a
    ///     stop this is today's <c>Task.WhenAll</c>; after the boundary each
    ///     straggler gets the grace period, then Abandoned — every call is
    ///     still answered, so the provider wire contract holds.
    /// </summary>
    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(
        Task<ToolResultEntry>[] tasks,
        IReadOnlyList<ToolCallPart> toolCalls,
        int count,
        CancellationToken ct)
    {
        Task<ToolResultEntry[]> all = Task.WhenAll(new ArraySegment<Task<ToolResultEntry>>(tasks, 0, count));
        await WaitForCompletionOrAcceptAsync(all, ct).ConfigureAwait(false);

        if (!ct.IsCancellationRequested)
        {
            // No stop: today's behavior — the batch result, or the original
            // fault when a dispatch truly broke (never Abandoned: no boundary
            // was crossed).
            return await all.ConfigureAwait(false);
        }

        if (all.IsCompletedSuccessfully)
        {
            // The stop landed after the last result: per-call Abandoned marking
            // already happened inside ExecuteWithRetryAsync; carry the batch.
            return all.Result;
        }

        logger.LogInformation(
            "Stop accepted ({Reason}); awaiting {Count} in-flight tool call(s) for {GraceMs:0}ms before abandoning",
            AcceptedStopReason, count, _abandonGrace.TotalMilliseconds);
        await Task.WhenAny(all, Task.Delay(_abandonGrace, _clock)).ConfigureAwait(false);

        var resolved = new ToolResultEntry[count];
        for (int i = 0; i < count; i++)
        {
            resolved[i] = tasks[i].IsCompletedSuccessfully
                ? tasks[i].Result
                : await AbandonLeftoverAsync(tasks[i], toolCalls[i]).ConfigureAwait(false);
        }

        return resolved;
    }

    /// <summary>
    ///     Report one call still running after the Accepted boundary plus grace
    ///     (#401 B2): an Abandoned error entry plus its terminal end event (the
    ///     card must stop spinning). The late task gets a fault observer, so its
    ///     exception is logged, never unobserved (§FP-003).
    /// </summary>
    private async Task<ToolResultEntry> AbandonLeftoverAsync(Task pending, ToolCallPart toolCall)
    {
        _ = pending.ContinueWith(
            t => logger.LogWarning(
                t.Exception,
                "Abandoned tool {ToolName} (call {CallId}) faulted after abandonment",
                toolCall.ToolName, toolCall.Id),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        string output =
            $"Tool '{toolCall.ToolName}' did not finish within {_abandonGrace.TotalMilliseconds:0}ms of grace " +
            $"after the accepted stop ({AcceptedStopReason}); call {ToolCallLink.AbandonedOutputMarker}.";
        var abandoned = ToolResult.Error(output);
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, abandoned, true), TerminalEventToken).ConfigureAwait(false);
        return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, abandoned);
    }

    /// <summary>
    ///     Text for a call that finished (against its token) after the stop was
    ///     accepted (#401 B2). Carries the named reason and the shared marker
    ///     <see cref="RunOutcome" /> keys <c>Abandoned</c> off.
    /// </summary>
    private static string AbandonedAfterFinishMessage(ToolCallPart toolCall) =>
        $"Tool '{toolCall.ToolName}' finished after the stop was accepted ({AcceptedStopReason}); " +
        $"call {ToolCallLink.AbandonedOutputMarker}.";

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
        if (resolved.IsFailure) // §4.6-ok: выход Bind-рельсы (ROP-C П.4), диагностики различаются по построению (L5).
        {
            return new ToolResultEntry(toolCall.Id, toolCall.ToolName, resolved.Error, true);
        }

        ITool tool = resolved.Value;

        // The tool's own Glyph rides the event (#680): every renderer draws the
        // icon from this field instead of keeping a tool-name-keyed table of its
        // own. Publishing it here is what makes adding a tool touch no UI code.
        await eventBus.PublishAsync(
            new ToolExecutionStartEvent(toolCall.Id, toolCall.ToolName, toolCall.Args, tool.Glyph),
            ct).ConfigureAwait(false);
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
            // #43: attempt counter lives outside the try so the error paths
            // below can report it (a catch cannot see try-block locals).
            var attempts = new AttemptCount();
            try
            {
            // Argument validation — returns a tool error instead of letting the
            // tool throw (e.g. KeyNotFoundException on a missing required prop).
            ToolResultEntry? invalid = await ValidateArgumentsAsync(tool, toolCall, activity, ct).ConfigureAwait(false);
            if (invalid is not null)
            {
                return invalid;
            }

            // Permission check.
            // #49 PR2: open the commit scope BEFORE the check so a cancel
            // landing anywhere in approve→commit invalidates it. Scopes are
            // epoch-scoped (parallel calls share fate), not once-only.
            long commitScope = coordinator?.BeginApprovalScope() ?? 0;

            ToolResultEntry? denied = await CheckPermissionAsync(
                toolCall, agent, effectiveCt, ct, activity).ConfigureAwait(false);
            if (denied is not null)
            {
                return denied;
            }

            // PX4: user PreToolUse hooks run after the policy gate — they can
            // only deny further, ask, or narrow the args, never widen policy.
            ToolCallPart effectiveCall = toolCall;
            if (_hooks is not null)
            {
                HookGate gate = await ApplyPreToolHooksAsync(
                    toolCall, session.Session.Id, effectiveCt, ct, activity).ConfigureAwait(false);
                if (gate.Refusal is not null)
                {
                    return gate.Refusal;
                }

                effectiveCall = gate.Call;
            }

            if (!ReferenceEquals(effectiveCall, toolCall))
            {
                // A hook edited the args: re-validate the edited payload the
                // same way the original was validated above (start event is
                // already published, so the failure entry pairs correctly).
                ToolResultEntry? editedInvalid = await ValidateArgumentsAsync(
                    tool, effectiveCall, activity, ct).ConfigureAwait(false);
                if (editedInvalid is not null)
                {
                    return editedInvalid;
                }
            }

            // Execution runs under the commit barrier with bounded retry
            // (#49 PR2/PR3, #43) — see ExecuteWithRetryAsync.
            var ctx = CreateToolContext(effectiveCall, session, partial, agent, effectiveCt);

            ToolResultEntry completed = await ExecuteWithRetryAsync(
                tool, effectiveCall, ctx, commitScope, attempts, activity, effectiveCt, ct).ConfigureAwait(false);

            // PX4: user PostToolUse hooks are advisory — the runner logs and
            // never throws on its own, so the completed entry is unaffected.
            if (_hooks is not null)
            {
                var completedResult = new ToolResult(completed.Output, completed.IsError);
                await _hooks.RunPostToolUseAsync(
                    effectiveCall.ToolName, effectiveCall.Args, completedResult,
                    session.Session.Id, ct).ConfigureAwait(false);
            }

            return completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelled = ToolResult.Error("Tool execution was cancelled.");
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, cancelled, true), TerminalEventToken).ConfigureAwait(false);
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
                toolCall.Id, timeout, true), TerminalEventToken).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, timeout);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "Tool {ToolName} failed", toolCall.ToolName);
            // Attempt count is reported only when retries actually happened —
            // the single-attempt message stays byte-identical (log/LLM stability).
            string errorMessage = attempts.Value > 1
                ? $"Tool execution failed after {attempts.Value} attempts: {ex.Message}"
                : $"Tool execution failed: {ex.Message}";
            var errored = ToolResult.Error(errorMessage);
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, errored, true), TerminalEventToken).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, errored);
        }
        }
    }

    /// <summary>Mutable attempt counter shared between the retry loop and the error formatter.</summary>
    private sealed class AttemptCount
    {
        public int Value;
    }

    /// <summary>
    ///     Validate tool arguments. Returns <see langword="null" /> when valid;
    ///     otherwise publishes the end event and returns the error entry.
    /// </summary>
    private async Task<ToolResultEntry?> ValidateArgumentsAsync(
        ITool tool,
        ToolCallPart toolCall,
        Activity? activity,
        CancellationToken ct)
    {
        var validation = tool.ValidateArguments(toolCall.Args);
        if (validation.IsSuccess)
        {
            return null;
        }

        activity?.SetStatus(ActivityStatusCode.Error, validation.Error);
        var invalid = ToolResult.Error(validation.Error);
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, invalid, true), TerminalEventToken).ConfigureAwait(false);
        return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, invalid);
    }

    /// <summary>
    ///     Check permission for one tool call. Returns <see langword="null" />
    ///     when allowed; otherwise publishes the end event and returns the deny
    ///     entry. Fail-closed: any non-success verdict denies (§G3).
    /// </summary>
    private async Task<ToolResultEntry?> CheckPermissionAsync(
        ToolCallPart toolCall,
        AgentDefinition agent,
        CancellationToken effectiveCt,
        CancellationToken ct,
        Activity? activity)
    {
        // #49 PR4: the invocation is born here (toolCall.Id). Plumb it
        // into the permission ask so the gate binds to this exact
        // attempt; approval is viewed once per dispatch (generation 1 —
        // retries re-enter under the same approval, never re-ask).
        var permResponse = await permissions.CheckAsync(
            agent.Name.Value, toolCall.ToolName, toolCall.Args, effectiveCt, toolCall.Id, generation: 1).ConfigureAwait(false);

        // G3 fail-closed: a permission-SUBSYSTEM failure (agent not in the
        // registry, invalid name) used to fall through to execution — i.e.
        // every tool ran as "allow all". Any non-success verdict now denies.
        // #1109: only an explicit Allow proceeds — a deferred Ask (asker
        // returned without approval) is fail-closed, never fail-open.
        if (permResponse.IsSuccess && permResponse.Value.Action == PermissionAction.Allow)
        {
            return null;
        }

        activity?.SetStatus(ActivityStatusCode.Error, "Permission denied");
        // Honest strings (#49 PR2): a deny produced by a cancelled wait
        // is a cancellation, not a policy decision.
        bool cancelled = ct.IsCancellationRequested;
        string reason = permResponse.IsFailure
            ? $"Permission check failed: {permResponse.Error}"
            : cancelled ? "Tool execution was cancelled before start."
            : "Permission denied";
        if (cancelled)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled before start");
        }

        var denied = ToolResult.Error(reason);
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, denied, true), TerminalEventToken).ConfigureAwait(false);
        return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, denied);
    }

    /// <summary>
    ///     Outcome of the PreToolUse hook gate: the call to execute (possibly
    ///     with hook-edited args) or a refusal entry that is already reported.
    /// </summary>
    private sealed record HookGate(ToolCallPart Call, ToolResultEntry? Refusal);

    /// <summary>
    ///     Run user PreToolUse hooks for one tool call. Deny (including the
    ///     runner's fail-closed deny on hook failure) and a declined ask
    ///     publish the end event and return a refusal entry; an accepted ask
    ///     and allow proceed, carrying any hook-edited args.
    /// </summary>
    private async Task<HookGate> ApplyPreToolHooksAsync(
        ToolCallPart toolCall,
        string sessionId,
        CancellationToken effectiveCt,
        CancellationToken ct,
        Activity? activity)
    {
        HookVerdict verdict;
        try
        {
            verdict = await _hooks!.RunPreToolUseAsync(
                toolCall.ToolName, toolCall.Args, sessionId, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The runner is contracted never to throw on its own; a failure
            // here is a hooks-subsystem failure, which fails closed to deny
            // exactly like the permission-subsystem failure (§G3).
            logger.LogWarning(ex, "PreToolUse hooks failed for {ToolName}; failing closed to Deny", toolCall.ToolName);
            verdict = new HookVerdict(HookDecision.Deny, $"Hook execution failed: {ex.Message}", null);
        }

        return await ResolveHookVerdictAsync(toolCall, verdict, effectiveCt, ct, activity).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fold one PreToolUse verdict into proceed/refuse. Ask routes to the
    ///     permission asker (real prompt when interactive, deny when headless);
    ///     an accepted ask proceeds with any hook-edited args.
    /// </summary>
    private async Task<HookGate> ResolveHookVerdictAsync(
        ToolCallPart toolCall,
        HookVerdict verdict,
        CancellationToken effectiveCt,
        CancellationToken ct,
        Activity? activity)
    {
        if (verdict.Decision == HookDecision.Allow)
        {
            ToolCallPart call = verdict.EditedArgs is { } edited && edited.ValueKind == JsonValueKind.Object
                ? toolCall with { Args = edited.Clone() }
                : toolCall;
            return new HookGate(call, null);
        }

        if (verdict.Decision == HookDecision.Ask)
        {
            Result<PermissionResponse> asked = await permissions.AskUserAsync(
                new PermissionRequest(
                    toolCall.ToolName,
                    "hook",
                    toolCall.Args,
                    new[] { "allow", "deny" },
                    toolCall.Id,
                    1),
                effectiveCt).ConfigureAwait(false);
            if (asked.IsSuccess && asked.Value.Action == PermissionAction.Allow)
            {
                ToolCallPart call = verdict.EditedArgs is { } edited && edited.ValueKind == JsonValueKind.Object
                    ? toolCall with { Args = edited.Clone() }
                    : toolCall;
                return new HookGate(call, null);
            }

            string askReason = asked.IsFailure
                ? $"Hook requested confirmation and the ask failed: {asked.Error}"
                : verdict.Reason ?? $"Hook '{toolCall.ToolName}' requested confirmation.";
            return await RefuseHook(toolCall, askReason, activity).ConfigureAwait(false);
        }

        return await RefuseHook(toolCall, verdict.Reason ?? $"Hook denied tool '{toolCall.ToolName}'.", activity).ConfigureAwait(false);
    }

    /// <summary>
    ///     Build the refusal entry for a denied/declined hook verdict, with its
    ///     terminal end event (mirrors the permission-deny path above).
    /// </summary>
    private async Task<HookGate> RefuseHook(ToolCallPart toolCall, string reason, Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Hook denied");
        logger.LogInformation("Tool {ToolName} (call {CallId}) refused by user hook: {Reason}",
            toolCall.ToolName, toolCall.Id, reason);
        var refused = ToolResult.Error(reason);
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, refused, true), TerminalEventToken).ConfigureAwait(false);
        return new HookGate(toolCall, ToolResultEntry.From(toolCall.Id, toolCall.ToolName, refused));
    }

    /// <summary>
    ///     Build the <see cref="ToolContext" /> for one call, wiring progress
    ///     reporting and the permission-ask callback.
    /// </summary>
    private ToolContext CreateToolContext(
        ToolCallPart toolCall,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken effectiveCt)
    {
        // Guard the GetRawText() call with IsEnabled — JsonElement.GetRawText()
        // allocates a fresh string every call, and LogDebug evaluates its args
        // eagerly before checking whether Debug is enabled. The guard eliminates
        // the per-tool-call string allocation when debug logging is off (the
        // common production case).
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Executing tool {ToolName} (call {CallId}) args={Args}", toolCall.ToolName, toolCall.Id, toolCall.Args.GetRawText());
        }

        return new ToolContext(
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
            // #201 B1: the Ask callback rides the AskUserAsync railway (checked,
            // never .Value) — a permission-subsystem failure fails closed to
            // Deny instead of throwing into the tool run (§ROP-002 recurrence).
            async (req, c) =>
            {
                Result<PermissionResponse> asked = await permissions.AskUserAsync(req, c).ConfigureAwait(false);
                if (asked.IsFailure)
                {
                    logger.LogWarning("Permission ask failed for {Permission} (call {CallId}); failing closed to Deny: {Error}",
                        req.Permission, toolCall.Id, asked.Error);
                    return new PermissionResponse(PermissionAction.Deny, false);
                }

                return asked.Value;
            },
            // S2 (#376): tools resolve their cwd from the session directory,
            // which SubAgentRunner binds to the isolated worktree. Main-session
            // directories are the user's cwd, so behaviour there is unchanged.
            session.Session.Directory);
    }

    /// <summary>
    ///     Execute one tool call with the commit barrier and bounded retry.
    ///     Cancel winning after approval but before the first tool instruction
    ///     prevents the START (#49 PR2/PR3) — token observation inside the tool
    ///     is best-effort only. The commit lives INSIDE the retry loop, bound to
    ///     (invocation, generation): each attempt commits its own generation,
    ///     duplicates and stale replays are rejected, and the record is retired
    ///     in finally so the registry holds only in-flight executions.
    /// </summary>
    private async Task<ToolResultEntry> ExecuteWithRetryAsync(
        ITool tool,
        ToolCallPart toolCall,
        ToolContext ctx,
        long commitScope,
        AttemptCount attempts,
        Activity? activity,
        CancellationToken effectiveCt,
        CancellationToken ct)
    {
        bool committed = false;
        try
        {
            // #43: bounded retry of transport-class failures. Only the bare
            // ExecuteAsync is retried — validation and permission already
            // happened. Each retry re-enters under the same approval (no
            // re-ask) but commits a NEW generation; cancellation between
            // attempts surfaces either at the commit, at the delay, or inside
            // the tool via the token.
            ToolResult result;
            while (true)
            {
                attempts.Value++;
                if (coordinator is not null
                    && !coordinator.TryCommitApproval(commitScope, toolCall.Id, attempts.Value))
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "cancelled before start");
                    var cancelledBeforeStart = ToolResult.Error("Tool execution was cancelled before start.");
                    await eventBus.PublishAsync(new ToolExecutionEndEvent(
                        toolCall.Id, cancelledBeforeStart, true), TerminalEventToken).ConfigureAwait(false);
                    return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, cancelledBeforeStart);
                }

                committed = true;
                try
                {
                    result = await tool.ExecuteAsync(toolCall.Args, ctx, effectiveCt).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                    {
                        // #401 B2: the stop was accepted while this call ran and
                        // the tool ignored its token. Reporting its success would
                        // read as work done after Stop — mark it Abandoned, with
                        // the terminal event to match. Reads the RUN token, not
                        // the per-call deadline: a timeout (A9) is a different
                        // fact and keeps its own text.
                        activity?.SetStatus(ActivityStatusCode.Error, ToolCallLink.AbandonedOutputMarker);
                        var abandonedAfterFinish = ToolResult.Error(AbandonedAfterFinishMessage(toolCall));
                        await eventBus.PublishAsync(new ToolExecutionEndEvent(
                            toolCall.Id, abandonedAfterFinish, true), TerminalEventToken).ConfigureAwait(false);
                        return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, abandonedAfterFinish);
                    }

                    break;
                }
                catch (Exception ex) when (retryDecider is not null
                    && !effectiveCt.IsCancellationRequested
                    && retryDecider.ShouldRetry(toolCall.ToolName, ex, attempts.Value))
                {
                    // OCE never reaches here (dedicated catches below); the
                    // filter also refuses to retry into a cancelled token, so
                    // the delay below can only throw on a raced cancel — which
                    // the same dedicated catches classify honestly.
                    TimeSpan backoff = RetryPolicy.ComputeDelay(retryDecider.Options, attempts.Value);
                    logger.LogWarning(ex, "Tool {ToolName} (call {CallId}) attempt {Attempt} transient, retrying in {BackoffMs:0}ms",
                        toolCall.ToolName, toolCall.Id, attempts.Value, backoff.TotalMilliseconds);
                    // #76: retry-projection feed (render-only). The UI mirrors
                    // attempt/max/backoff from these fields; the Task.Delay below
                    // stays the only scheduling authority — the UI never triggers.
                    await eventBus.PublishAsync(new ToolExecutionUpdateEvent(
                        toolCall.Id,
                        $"retry {attempts.Value}/{retryDecider.Options.MaxAttempts} in {backoff.TotalSeconds:0.#}s",
                        attempts.Value,
                        retryDecider.Options.MaxAttempts,
                        backoff.TotalSeconds), ct).ConfigureAwait(false);
                    await Task.Delay(backoff, effectiveCt).ConfigureAwait(false);
                }
            }

            logger.LogDebug("Tool execution end: {ToolName} (call {CallId}) isError={IsError}", toolCall.ToolName, toolCall.Id, result.IsError);
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, result, result.IsError), TerminalEventToken).ConfigureAwait(false);

            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, result);
        }
        finally
        {
            if (committed)
            {
                coordinator?.CompleteInvocation(toolCall.Id);
            }
        }
    }
}
