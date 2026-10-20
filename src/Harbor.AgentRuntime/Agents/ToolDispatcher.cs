using System.Buffers;
using System.Diagnostics;
using Harbor.Abstractions.Extensions;
using Harbor.Application.Configuration;
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
///         <b>Scheduling (#1766):</b> the batch is scheduled PER CALL, not per
///         batch. A call whose tool declares
///         <see cref="ExecutionMode.Sequential" /> is EXCLUSIVE — a barrier:
///         the parallel-safe group before it drains first, it runs alone, and
///         the group after it starts only once it has finished. Every other
///         call runs in a pool capped at the configured maximum
///         (<see cref="ToolingConfig.MaxParallelToolCalls" />), and the excess
///         waits for a slot instead of starting at once. Before #1766 the
///         decision was taken once per batch, so ONE sequential call made all
///         five reads serial too.
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
    IHookRunner? hookRunner = null,
    // #1766: the cap on concurrent parallel-safe tool calls. Null keeps the
    // documented default; the host injects the configured value.
    int? maxParallelToolCalls = null) : IToolDispatcher
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
    ///     The cap on concurrent parallel-safe tool calls in one batch
    ///     (#1766). One slot per running call; the excess waits for a slot
    ///     instead of starting at once. Clamped to the accepted range, so a
    ///     mis-set number serialises the batch instead of throwing at
    ///     dispatch time.
    /// </summary>
    private readonly int _maxParallelToolCalls = ToolBatchScheduler.NormalizeCap(
        maxParallelToolCalls ?? ToolingConfig.DefaultMaxParallelToolCalls);

    // #1507 (#95 slice): validate/permission/retry chain lives in ToolRetryPolicy
    // (verbatim move); this dispatcher keeps thin forwarders so the
    // ExecuteSingleAsync call sites are untouched.
    private readonly ToolRetryPolicy _retryPolicy = new(eventBus, permissions, logger, coordinator, retryDecider);

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
    ///     Execute a batch of tool calls: parallel-safe calls share a capped
    ///     pool, and a call that declares
    ///     <see cref="ExecutionMode.Sequential" /> runs alone as a barrier
    ///     (#1766). Returns a <see cref="ToolResultMessage" /> ready to append
    ///     to the session.
    /// </summary>
    public async Task<ToolResultMessage> ExecuteAsync(
        IReadOnlyList<ToolCallPart> toolCalls,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout = null)
    {
        // #1100: resolve once per call. The old sequential gate used to
        // resolve every name (TryCreate+GetTool), then ExecuteSingleAsync
        // resolved the same names again — 2N registry hits per N-call turn.
        // One pass here feeds both the scheduler and dispatch.
        var resolved = new Result<ITool>[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
        {
            resolved[i] = ResolveTool(toolCalls[i].ToolName);
        }

        var results = new List<ToolResultEntry>(toolCalls.Count);

        // #1766: the schedule is decided PER CALL, not per batch. The batch
        // walks left to right; a parallel-safe run goes to the capped pool,
        // and an exclusive call is a barrier — the group before it has
        // drained by the time it runs, and the group after it starts only
        // once it has finished.
        int cursor = 0;
        while (cursor < toolCalls.Count)
        {
            if (ToolBatchScheduler.IsExclusive(resolved[cursor]))
            {
                results.Add(await RunExclusiveAsync(
                    cursor,
                    toolCalls,
                    resolved,
                    session,
                    partial,
                    agent,
                    ct,
                    toolExecutionTimeout).ConfigureAwait(false));
                cursor++;
                continue;
            }

            int end = ToolBatchScheduler.EndOfParallelGroup(resolved, cursor);
            results.AddRange(await RunParallelGroupAsync(
                cursor,
                end,
                toolCalls,
                resolved,
                session,
                partial,
                agent,
                ct,
                toolExecutionTimeout).ConfigureAwait(false));
            cursor = end;
        }

        return new ToolResultMessage(
            Guid.NewGuid().ToString("N"),
            session.Session.Id,
            DateTimeOffset.UtcNow,
            results);
    }

    /// <summary>
    ///     Run one EXCLUSIVE call alone (#1766) — the barrier. Nothing else
    ///     of the batch is in flight when it starts, and nothing starts
    ///     until it has finished.
    /// </summary>
    private async Task<ToolResultEntry> RunExclusiveAsync(
        int index,
        IReadOnlyList<ToolCallPart> toolCalls,
        Result<ITool>[] resolved,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout)
    {
        var tc = toolCalls[index];

        // #401: an accepted stop ends NEW work. A barrier is exactly where
        // the gap is widest — the previous group has just drained, and the
        // next one has not started.
        if (ct.IsCancellationRequested)
        {
            return NotStartedBecauseStopped(tc);
        }

        // #401 B2: the in-flight side of the same boundary — an exclusive
        // call that outlives the stop gets the grace, then Abandoned,
        // instead of holding the run open indefinitely.
        Task<ToolResultEntry> pending = ExecuteSingleAsync(
            tc, resolved[index], session, partial, agent, ct, toolExecutionTimeout);
        return await AwaitOneWithAbandonAsync(pending, tc, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Run one parallel-safe group <c>[start, end)</c> in a pool capped
    ///     at <c>_maxParallelToolCalls</c> (#1766). The excess waits for a
    ///     slot instead of starting at once, so the number of calls in flight
    ///     never exceeds the cap.
    /// </summary>
    private async Task<ToolResultEntry[]> RunParallelGroupAsync(
        int start,
        int end,
        IReadOnlyList<ToolCallPart> toolCalls,
        Result<ITool>[] resolved,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout)
    {
        int count = end - start;
        Task<ToolResultEntry>[]? tasks = null;
        try
        {
            // Rent the task array from the ArrayPool — Task.WhenAll accepts an IEnumerable<Task>,
            // so we can pass a Span-based slice without the secondary ToArray() allocation.
            // The pooled array is cleared before return so the Task references don't keep
            // the underlying async state machines alive longer than necessary.
            tasks = ArrayPool<Task<ToolResultEntry>>.Shared.Rent(count);

            // #1766: the cap. One slot per running call; the group may never
            // have more than the cap in flight.
            using var gate = new SemaphoreSlim(_maxParallelToolCalls, _maxParallelToolCalls);

            for (int k = 0; k < count; k++)
            {
                int index = start + k;
                // #401: the dispatch loop is the acceptance boundary. Each
                // iteration is a fresh `ExecuteSingleAsync` invocation, so a
                // cancel landing between iterations would otherwise start
                // calls the user asked to stop. Dispatch is cheap and
                // synchronous up to the permission gate, so the window is
                // real (a permission-gate cancel from iteration k-1 lands
                // inside this loop) rather than theoretical.
                tasks[k] = ct.IsCancellationRequested
                    ? Task.FromResult(NotStartedBecauseStopped(toolCalls[index]))
                    : RunGatedAsync(
                        index,
                        gate,
                        toolCalls,
                        resolved,
                        session,
                        partial,
                        agent,
                        ct,
                        toolExecutionTimeout);
            }

            return await AwaitAllWithAbandonAsync(
                tasks, toolCalls, count, start, ct).ConfigureAwait(false);
        }
        finally
        {
            if (tasks is not null)
            {
                Array.Clear(tasks, 0, count);
                ArrayPool<Task<ToolResultEntry>>.Shared.Return(tasks);
            }
        }
    }

    /// <summary>
    ///     Run one call under the group's concurrency cap (#1766). The call
    ///     waits for a slot; it does not start until it has one, and the
    ///     number of calls in flight never exceeds the cap.
    /// </summary>
    private async Task<ToolResultEntry> RunGatedAsync(
        int index,
        SemaphoreSlim gate,
        IReadOnlyList<ToolCallPart> toolCalls,
        Result<ITool>[] resolved,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout)
    {
        // The wait for a slot is cancellable: a stop while a call is queued
        // ends the wait — it does not leave the call hanging on the gate,
        // and it does not swallow the token.
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // #401: the call never started, so it is a "not started" entry —
            // not an abandoned one, and it publishes no start event.
            return NotStartedBecauseStopped(toolCalls[index]);
        }

        try
        {
            // The token may have fired between the dispatch-loop check and
            // the slot acquisition (#401): re-check before the tool runs.
            if (ct.IsCancellationRequested)
            {
                return NotStartedBecauseStopped(toolCalls[index]);
            }

            return await ExecuteSingleAsync(
                toolCalls[index],
                resolved[index],
                session,
                partial,
                agent,
                ct,
                toolExecutionTimeout).ConfigureAwait(false);
        }
        finally
        {
            // The group can be over while this call is still running — the
            // #401 abandon path gives a stuck call a bounded grace and then
            // reports it Abandoned without waiting for it. The gate is
            // disposed by then, and releasing a slot on a disposed semaphore
            // throws, so the release is guarded: the slot died with the
            // group.
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // The group already finished; nothing is waiting for this slot.
            }
        }
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
        ToolDispatcherLog.ToolNotStartedStopAccepted(logger, toolCall.ToolName, toolCall.Id);
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
            // #1099: async read — IsCompletedSuccessfully guards the wait,
            // not a blocking take.
            return await pending.ConfigureAwait(false);
        }

        ToolDispatcherLog.StopAcceptedAwaitingOne(logger, AcceptedStopReason, toolCall.Id, _abandonGrace.TotalMilliseconds);
        await Task.WhenAny(pending, Task.Delay(_abandonGrace, _clock)).ConfigureAwait(false);

        return pending.IsCompletedSuccessfully
            ? await pending.ConfigureAwait(false)
            : await AbandonLeftoverAsync(pending, toolCall).ConfigureAwait(false);
    }

    /// <summary>
    ///     Accepted-boundary wait for a parallel batch (#401 B2). Without a
    ///     stop this is today's <c>Task.WhenAll</c>; after the boundary each
    ///     straggler gets the grace period, then Abandoned — every call is
    ///     still answered, so the provider wire contract holds.
    /// </summary>
    /// <param name="tasks">
    ///     The group's tasks, indexed from 0 — the group's own slice, not the
    ///     whole batch.
    /// </param>
    /// <param name="toolCalls">The whole batch's calls.</param>
    /// <param name="count">How many of <paramref name="tasks" /> belong to the group.</param>
    /// <param name="toolCallOffset">
    ///     Where the group starts in <paramref name="toolCalls" /> (#1766: the
    ///     batch is scheduled in groups, and the group is a slice of it).
    /// </param>
    /// <param name="ct">The run token.</param>
    private async Task<ToolResultEntry[]> AwaitAllWithAbandonAsync(
        Task<ToolResultEntry>[] tasks,
        IReadOnlyList<ToolCallPart> toolCalls,
        int count,
        int toolCallOffset,
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
            // #1099: async read — IsCompletedSuccessfully guards the wait,
            // not a blocking take.
            return await all.ConfigureAwait(false);
        }

        ToolDispatcherLog.StopAcceptedAwaitingMany(logger, AcceptedStopReason, count, _abandonGrace.TotalMilliseconds);
        await Task.WhenAny(all, Task.Delay(_abandonGrace, _clock)).ConfigureAwait(false);

        var resolved = new ToolResultEntry[count];
        for (int i = 0; i < count; i++)
        {
            resolved[i] = tasks[i].IsCompletedSuccessfully
                ? await tasks[i].ConfigureAwait(false)
                : await AbandonLeftoverAsync(tasks[i], toolCalls[toolCallOffset + i]).ConfigureAwait(false);
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
            t => ToolDispatcherLog.AbandonedToolFaulted(logger, t.Exception!, toolCall.ToolName, toolCall.Id),
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
    ///     Execute a single tool call: validate name → validate args → check
    ///     permission → publish start event → execute → publish end event.
    ///     All error paths return a <see cref="ToolResultEntry" /> with
    ///     <c>IsError=true</c> rather than throwing.
    ///     #1100: the name→tool railway already ran once per call in
    ///     ExecuteAsync — the pre-resolved outcome rides in, no second lookup.
    /// </summary>
    private async Task<ToolResultEntry> ExecuteSingleAsync(
        ToolCallPart toolCall,
        Result<ITool> resolved,
        ISessionContext session,
        AssistantMessage partial,
        AgentDefinition agent,
        CancellationToken ct,
        TimeSpan? toolExecutionTimeout = null)
    {
        using var activity = Source.StartActivity("Tool.Execute");
        activity?.SetTag(ToolNameTag, toolCall.ToolName);

        // §4.6-ok: выход Bind-рельсы (ROP-C П.4), диагностики различаются по
        // построению (L5) — текст приехал из ResolveTool в ExecuteAsync.
        if (resolved.IsFailure)
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
        ToolDispatcherLog.ToolExecutionStart(logger, toolCall.ToolName, toolCall.Id);

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
            var attempts = new ToolRetryPolicy.AttemptCount();
            try
            {
            // Validate → permission on one railway (#1515): each gate returns
            // UnitResult<string> (success = pass, failure carries the entry
            // text); the terminal publish already rode the failure branch
            // inside the gate, so the fold below only materializes the entry.
            // #49 PR2: the commit scope opens BEFORE the permission check so
            // a cancel landing anywhere in approve→commit invalidates it.
            // Scopes are epoch-scoped (parallel calls share fate), not
            // once-only.
            long commitScope = 0;
            UnitResult<string> gates = await ValidateArgumentsAsync(tool, toolCall, activity, ct)
                .Bind(() =>
                {
                    commitScope = coordinator?.BeginApprovalScope() ?? 0;
                    return CheckPermissionAsync(toolCall, agent, effectiveCt, ct, activity);
                })
                .ConfigureAwait(false);
            if (gates.IsFailure)
            {
                return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, ToolResult.Error(gates.Error));
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
                UnitResult<string> editedValid = await ValidateArgumentsAsync(
                    tool, effectiveCall, activity, ct).ConfigureAwait(false);
                if (editedValid.IsFailure)
                {
                    return ToolResultEntry.From(effectiveCall.Id, effectiveCall.ToolName, ToolResult.Error(editedValid.Error));
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
            ToolDispatcherLog.ToolExecutionDeadlineHit(logger, oce, toolCall.ToolName, toolCall.Id);
            var timeout = ToolResult.Error(message);
            await eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, timeout, true), TerminalEventToken).ConfigureAwait(false);
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, timeout);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            ToolDispatcherLog.ToolFailed(logger, ex, toolCall.ToolName);
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

    /// <summary>
    ///     Validate tool arguments on the Result railway (#1515).
    ///     Forwarder over <see cref="ToolRetryPolicy" /> (#1507, #95 slice: verbatim move).
    /// </summary>
    private Task<UnitResult<string>> ValidateArgumentsAsync(
        ITool tool,
        ToolCallPart toolCall,
        Activity? activity,
        CancellationToken ct) =>
        _retryPolicy.ValidateArgumentsAsync(tool, toolCall, activity, ct);

    /// <summary>
    ///     Check permission for one tool call on the Result railway (#1515).
    ///     Forwarder over <see cref="ToolRetryPolicy" /> (#1507, #95 slice: verbatim move).
    /// </summary>
    private Task<UnitResult<string>> CheckPermissionAsync(
        ToolCallPart toolCall,
        AgentDefinition agent,
        CancellationToken effectiveCt,
        CancellationToken ct,
        Activity? activity) =>
        _retryPolicy.CheckPermissionAsync(toolCall, agent, effectiveCt, ct, activity);

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
            ToolDispatcherLog.PreToolUseHooksFailed(logger, ex, toolCall.ToolName);
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
        ToolDispatcherLog.ToolRefusedByHook(logger, toolCall.ToolName, toolCall.Id, reason);
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
            ToolDispatcherLog.ExecutingTool(logger, toolCall.ToolName, toolCall.Id, toolCall.Args.GetRawText());
        }

        // #1529: the two callbacks used to be inline async lambdas, and each
        // one compiled to a closure display-class — the progress lambda
        // captured the event bus, the logger and the tool call, the ask
        // lambda captured the permission service, the logger and the tool
        // call — so every tool call paid for two display-classes on the
        // per-tool-call hot path. The bodies never varied, only the captured
        // state did, so they now live in one small sealed state record per
        // call — the issue's small sealed state record option — whose two
        // bodies are static and take that record as an explicit state
        // parameter, and the two delegates bind to the record as their
        // target. The display-class allocations are gone and the public
        // ToolContext delegate signatures are untouched. Behaviour stays
        // byte-identical: the progress publish is still awaited with failures
        // only logged, and Ask still fails closed to Deny on any permission
        // failure.
        ToolCallGates gates = new(eventBus, logger, permissions, toolCall);
        return new ToolContext(
            session.Session.Id,
            partial.Id,
            toolCall.Id,
            agent.Name.Value,
            effectiveCt,
            session.Messages,
            gates.ReportProgress,
            gates.Ask,
            // S2 (#376): tools resolve their cwd from the session directory,
            // which SubAgentRunner binds to the isolated worktree. Main-session
            // directories are the user's cwd, so behaviour there is unchanged.
            session.Session.Directory);
    }

    /// <summary>
    ///     #1529: the per-call state behind the two <see cref="ToolContext" />
    ///     callbacks, and the two callback bodies that used to be inline
    ///     lambdas in <see cref="CreateToolContext" />.
    ///     <para>
    ///         Both bodies are static and take this record as an explicit
    ///         state parameter, and the two delegates bind to this record as
    ///         their target — so a tool call no longer compiles a closure
    ///         display-class for either callback. The bodies are one-to-one
    ///         with the former lambdas: the progress publish is awaited and
    ///         only its failures are logged, and the ask path fails closed to
    ///         Deny whenever the permission service itself fails.
    ///     </para>
    /// </summary>
    private sealed class ToolCallGates(
        IEventBus eventBus,
        ILogger logger,
        IPermissionService permissions,
        ToolCallPart toolCall)
    {
        /// <summary>The event bus this call's progress updates go to.</summary>
        private IEventBus EventBus => eventBus;

        /// <summary>The logger this call's callback failures are logged to.</summary>
        private ILogger Logger => logger;

        /// <summary>The permission service this call asks for decisions.</summary>
        private IPermissionService Permissions => permissions;

        /// <summary>The tool call this context was built for.</summary>
        private ToolCallPart ToolCall => toolCall;

        /// <summary>
        ///     The per-call progress callback. Bound to this record, so it
        ///     costs no closure display-class per call.
        /// </summary>
        public Task ReportProgress(ToolProgressUpdate update, CancellationToken c) =>
            ReportProgressCore(this, update, c);

        /// <summary>
        ///     The per-call ask callback. Bound to this record, so it costs
        ///     no closure display-class per call.
        /// </summary>
        public Task<PermissionResponse> Ask(PermissionRequest req, CancellationToken c) =>
            AskCore(this, req, c);

        /// <summary>
        ///     The body of the per-call progress callback (was an async lambda
        ///     capturing the event bus, the logger and the tool call).
        /// </summary>
        private static async Task ReportProgressCore(
            ToolCallGates state,
            ToolProgressUpdate update,
            CancellationToken c)
        {
            // §FP-003 (RESOLVED): previously the publish was fire-and-forget
            // — exceptions died as unobserved task exceptions and tool
            // progress updates were silently dropped on bus back-pressure.
            // The body awaits the publish with a guard around it so failures
            // are logged without breaking tool execution. Return type is
            // still Task per the ToolContext.ReportProgress contract.
            try
            {
                await state.EventBus.PublishAsync(
                        new ToolExecutionUpdateEvent(state.ToolCall.Id, update.PartialResult ?? update), c)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ToolDispatcherLog.ToolProgressPublishFailed(state.Logger, ex, state.ToolCall.Id);
            }
        }

        /// <summary>
        ///     The body of the per-call ask callback (was an async lambda
        ///     capturing the permission service, the logger and the tool call).
        /// </summary>
        private static async Task<PermissionResponse> AskCore(
            ToolCallGates state,
            PermissionRequest req,
            CancellationToken c)
        {
            // #201 B1: the Ask callback rides the AskUserAsync railway
            // (checked, never .Value) — a permission-subsystem failure fails
            // closed to Deny instead of throwing into the tool run (§ROP-002
            // recurrence).
            Result<PermissionResponse> asked = await state.Permissions.AskUserAsync(req, c).ConfigureAwait(false);
            if (asked.IsFailure)
            {
                ToolDispatcherLog.PermissionAskFailed(state.Logger, req.Permission, state.ToolCall.Id, asked.Error);
                return new PermissionResponse(PermissionAction.Deny, false);
            }

            return asked.Value;
        }
    }

    /// <summary>
    ///     Execute one tool call with the commit barrier and bounded retry (#49 PR2/PR3, #43).
    ///     Forwarder over <see cref="ToolRetryPolicy" /> (#1507, #95 slice: verbatim move).
    /// </summary>
    private Task<ToolResultEntry> ExecuteWithRetryAsync(
        ITool tool,
        ToolCallPart toolCall,
        ToolContext ctx,
        long commitScope,
        ToolRetryPolicy.AttemptCount attempts,
        Activity? activity,
        CancellationToken effectiveCt,
        CancellationToken ct) =>
        _retryPolicy.ExecuteWithRetryAsync(tool, toolCall, ctx, commitScope, attempts, activity, effectiveCt, ct);
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="ToolDispatcher" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ToolDispatcherLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Tool {ToolName} (call {CallId}) not started — a stop was accepted before dispatch")]
    public static partial void ToolNotStartedStopAccepted(ILogger logger, string toolName, string callId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Stop accepted ({Reason}); awaiting in-flight tool call {CallId} for {GraceMs:0}ms before abandoning")]
    public static partial void StopAcceptedAwaitingOne(ILogger logger, string reason, string callId, double graceMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stop accepted ({Reason}); awaiting {Count} in-flight tool call(s) for {GraceMs:0}ms before abandoning")]
    public static partial void StopAcceptedAwaitingMany(ILogger logger, string reason, int count, double graceMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Abandoned tool {ToolName} (call {CallId}) faulted after abandonment")]
    public static partial void AbandonedToolFaulted(ILogger logger, Exception ex, string toolName, string callId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Tool execution start: {ToolName} (call {CallId})")]
    public static partial void ToolExecutionStart(ILogger logger, string toolName, string callId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Tool {ToolName} (call {CallId}) hit its execution deadline")]
    public static partial void ToolExecutionDeadlineHit(ILogger logger, Exception ex, string toolName, string callId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Tool {ToolName} failed")]
    public static partial void ToolFailed(ILogger logger, Exception ex, string toolName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "PreToolUse hooks failed for {ToolName}; failing closed to Deny")]
    public static partial void PreToolUseHooksFailed(ILogger logger, Exception ex, string toolName);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Tool {ToolName} (call {CallId}) refused by user hook: {Reason}")]
    public static partial void ToolRefusedByHook(ILogger logger, string toolName, string callId, string reason);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Executing tool {ToolName} (call {CallId}) args={Args}")]
    public static partial void ExecutingTool(ILogger logger, string toolName, string callId, string args);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Tool progress publish failed for {ToolCallId}")]
    public static partial void ToolProgressPublishFailed(ILogger logger, Exception ex, string toolCallId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Permission ask failed for {Permission} (call {CallId}); failing closed to Deny: {Error}")]
    public static partial void PermissionAskFailed(ILogger logger, string permission, string callId, string error);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Tool {ToolName} (call {CallId}) attempt {Attempt} transient, retrying in {BackoffMs:0}ms")]
    public static partial void ToolAttemptTransient(ILogger logger, Exception ex, string toolName, string callId, int attempt, double backoffMs);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Tool execution end: {ToolName} (call {CallId}) isError={IsError}")]
    public static partial void ToolExecutionEnd(ILogger logger, string toolName, string callId, bool isError);

}
