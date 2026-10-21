using System.Diagnostics;
using Harbor.Application.Resilience;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     Validate/permission/retry chain extracted from <see cref="ToolDispatcher" />
///     (#1507, #95 slice): argument validation, the permission gate, and the
///     commit-barrier + bounded-retry execution loop. Verbatim move — the
///     dispatcher's three private helpers, unchanged.
/// </summary>
/// <remarks>
///     Shares the dispatcher's logger so log categories stay identical to
///     pre-extraction. Dispatch/abandon orchestration
///     (<c>ExecuteAsync</c>/<c>ExecuteSingleAsync</c>/<c>Await*WithAbandonAsync</c>)
///     stays in <see cref="ToolDispatcher" /> — caller migration + forwarder
///     deletion ride in #1392.
/// </remarks>
internal sealed class ToolRetryPolicy(
    IEventBus eventBus,
    IPermissionService permissions,
    ILogger logger,
    // #49 PR2: execution-commit barrier. Null (tests, manual construction)
    // keeps the legacy token-only path.
    IApprovalCoordinator? coordinator = null,
    // #43: retry decider (decision only; backoff via RetryPolicy.ComputeDelay).
    // Null keeps legacy no-retry behavior for direct constructions.
    IToolRetryDecider? retryDecider = null)
{
    /// <summary>
    ///     Publish token for the TERMINAL event of a tool call (#401).
    ///     Mirrors <see cref="ToolDispatcher" />: an already-cancelled token
    ///     must not gate the terminal record of a cancelled call.
    /// </summary>
    private static readonly CancellationToken TerminalEventToken = CancellationToken.None;

    /// <summary>Mutable attempt counter shared between the retry loop and the error formatter.</summary>
    internal sealed class AttemptCount
    {
        public int Value;
    }

    /// <summary>
    ///     Text for a call that finished (against its token) after the stop was
    ///     accepted (#401 B2). Carries the named reason and the shared marker
    ///     <see cref="RunOutcome" /> keys <c>Abandoned</c> off.
    /// </summary>
    private static string AbandonedAfterFinishMessage(ToolCallPart toolCall) =>
        $"Tool '{toolCall.ToolName}' finished after the stop was accepted ({ToolDispatcher.AcceptedStopReason}); " +
        $"call {ToolCallLink.AbandonedOutputMarker}.";

    /// <summary>
    ///     Make sure a failed result carries a code, and that its text leads
    ///     with that code (#1760).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A tool that reports a failure without classifying it gets the
    ///         generic <see cref="ToolErrorCodes.Error" />: the dispatcher does
    ///         not guess WHY the tool failed — it only says that it did, and
    ///         the model gets a code instead of a prose puzzle.
    ///     </para>
    ///     <para>
    ///         A tool that DID classify it keeps its own code, and the text is
    ///         put in front of the detail if the tool did not do it itself.
    ///         The invariant "a failed tool result's text starts with its
    ///         code" is what lets a reader (or a model) take the first token
    ///         as the code, so it is enforced here rather than trusted.
    ///     </para>
    /// </remarks>
    /// <param name="result">The result the tool returned.</param>
    /// <returns>The result with the invariant applied; a success is returned unchanged.</returns>
    private static ToolResult NormalizeErrorCode(ToolResult result)
    {
        if (!result.IsError)
        {
            return result;
        }

        if (result.ErrorInfo is null)
        {
            return new ToolResult(
                ToolErrorCodes.Format(ToolErrorCodes.Error, result.Output),
                true,
                result.Metadata,
                result.Attachments)
            {
                ErrorInfo = ToolErrorCodes.Create(ToolErrorCodes.Error),
            };
        }

        string codePrefix = result.ErrorInfo.Code + ": ";
        if (result.Output.StartsWith(codePrefix, StringComparison.Ordinal))
        {
            return result;
        }

        return new ToolResult(codePrefix + result.Output, true, result.Metadata, result.Attachments)
        {
            ErrorInfo = result.ErrorInfo,
        };
    }

    /// <summary>
    ///     Validate tool arguments on the Result railway (#1515): success
    ///     (no value) when valid; otherwise publishes the end event and
    ///     returns a failure carrying the finished error result.
    /// </summary>
    /// <remarks>
    ///     #1760: the failure carries the whole <see cref="ToolResult" /> —
    ///     text AND the structured code — so the dispatcher never has to
    ///     re-derive the code from the text it is handed.
    /// </remarks>
    internal async Task<UnitResult<ToolResult>> ValidateArgumentsAsync(
        ITool tool,
        ToolCallPart toolCall,
        Activity? activity,
        CancellationToken ct)
    {
        Result check = tool.ValidateArguments(toolCall.Args);
        if (check.IsSuccess)
        {
            return UnitResult.Success<ToolResult>();
        }

        activity?.SetStatus(ActivityStatusCode.Error, check.Error);
        var invalid = ToolResult.Failed(
            check.Error,
            ToolErrorCodes.Create(ToolErrorCodes.ValidationFailed, check.Error));
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, invalid, true), TerminalEventToken).ConfigureAwait(false);
        return UnitResult.Failure<ToolResult>(invalid);
    }

    /// <summary>
    ///     Check permission for one tool call on the Result railway (#1515):
    ///     success (no value) when allowed; otherwise publishes the end
    ///     event and returns a failure carrying the finished error result.
    ///     Fail-closed: any non-success verdict denies (§G3).
    /// </summary>
    /// <remarks>
    ///     #1760: the refusals are different facts and now carry different
    ///     codes — the run was cancelled
    ///     (<see cref="ToolErrorCodes.AbortedBeforeDispatch" />), the deadline
    ///     fired (<see cref="ToolErrorCodes.Timeout" />), the policy refused
    ///     (<see cref="ToolErrorCodes.PermissionDenied" />), or the permission
    ///     subsystem itself broke (<see cref="ToolErrorCodes.PermissionFailed" />).
    /// </remarks>
    internal async Task<UnitResult<ToolResult>> CheckPermissionAsync(
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
            return UnitResult.Success<ToolResult>();
        }

        activity?.SetStatus(ActivityStatusCode.Error, "Permission denied");
        // Honest strings (#49 PR2): a deny produced by a cancelled wait
        // is a cancellation, not a policy decision.
        // #1103: read the EFFECTIVE token, not the run token. The gate runs
        // under the per-call deadline: when the deadline fires, the linked
        // token cancels while the run token stays live, and the old run-token
        // read reported the deadline hit as a policy deny. A deadline hit is
        // a timeout (the loop keeps going); only a cancelled run token is a
        // user cancel.
        bool runCancelled = ct.IsCancellationRequested;
        bool timedOut = !runCancelled && effectiveCt.IsCancellationRequested;
        string code;
        string reason;
        string? cause = null;
        if (runCancelled)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled before start");
            code = ToolErrorCodes.AbortedBeforeDispatch;
            reason = "Tool execution was cancelled before start.";
        }
        else if (timedOut)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "tool timed out");
            code = ToolErrorCodes.Timeout;
            reason = $"Tool '{toolCall.ToolName}' timed out during permission check.";
        }
        else if (permResponse.IsFailure)
        {
            code = ToolErrorCodes.PermissionFailed;
            reason = $"Permission check failed: {permResponse.Error}";
            cause = permResponse.Error;
        }
        else
        {
            code = ToolErrorCodes.PermissionDenied;
            reason = "Permission denied";
        }

        var denied = ToolResult.Failed(
            reason,
            ToolErrorCodes.Create(code, cause));
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, denied, true), TerminalEventToken).ConfigureAwait(false);
        return UnitResult.Failure<ToolResult>(denied);
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
    internal async Task<ToolResultEntry> ExecuteWithRetryAsync(
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
                    // #1760: the call was never sent to the tool — it had no
                    // effect, so a retry is safe. The code says so; the text
                    // always said so.
                    var cancelledBeforeStart = ToolResult.Failed(
                        "Tool execution was cancelled before start.",
                        ToolErrorCodes.Create(ToolErrorCodes.AbortedBeforeDispatch));
                    await eventBus.PublishAsync(new ToolExecutionEndEvent(
                        toolCall.Id, cancelledBeforeStart, true), TerminalEventToken).ConfigureAwait(false);
                    return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, cancelledBeforeStart);
                }

                committed = true;
                try
                {
                    result = await tool.ExecuteAsync(toolCall.Args, ctx, effectiveCt).ConfigureAwait(false);
                    result = NormalizeErrorCode(result);
                    if (ct.IsCancellationRequested)
                    {
                        // #401 B2: the stop was accepted while this call ran and
                        // the tool ignored its token. Reporting its success would
                        // read as work done after Stop — mark it Abandoned, with
                        // the terminal event to match. Reads the RUN token, not
                        // the per-call deadline: a timeout (A9) is a different
                        // fact and keeps its own text.
                        activity?.SetStatus(ActivityStatusCode.Error, ToolCallLink.AbandonedOutputMarker);
                        // #1760: the code is TOOL_ABANDONED; the marker stays in
                        // the text so a session file written before the
                        // structured code existed still reads as abandoned.
                        var abandonedAfterFinish = ToolResult.Failed(
                            AbandonedAfterFinishMessage(toolCall),
                            ToolErrorCodes.Create(ToolErrorCodes.Abandoned, ToolDispatcher.AcceptedStopReason));
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
                    ToolDispatcherLog.ToolAttemptTransient(logger, ex, toolCall.ToolName, toolCall.Id, attempts.Value, backoff.TotalMilliseconds);
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

            ToolDispatcherLog.ToolExecutionEnd(logger, toolCall.ToolName, toolCall.Id, result.IsError);
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
