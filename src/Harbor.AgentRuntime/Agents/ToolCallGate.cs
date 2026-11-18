using System.Diagnostics;
using Harbor.Application.Hooks;
using Harbor.Application.Resilience;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     Per-tool-call gate extracted from <see cref="ToolDispatcher" /> (#2579,
///     audit finding F2): the contiguous validate → permission → PreToolUse
///     hook railway that decides whether a resolved call proceeds, plus the
///     commit-barrier retry execution that follows a successful verdict.
/// </summary>
/// <remarks>
///     <para>
///         Kept as a thin collaborator over <see cref="ToolRetryPolicy" /> so
///         the dispatcher no longer owns policy decisions. Bodies are a
///         verbatim move; log templates, terminal-event tokens, error codes,
///         and the event sequence are unchanged. The dispatcher's private
///         helpers become one-line forwarders (#1507 pattern) so the
///         <c>ExecuteSingleAsync</c> call sites are untouched.
///     </para>
///     <para>
///         Hooks are optional: when no <see cref="IHookRunner" /> is injected
///         the gate mirrors the legacy hooks-free path (every verdict allows
///         and the call proceeds unchanged).
///     </para>
/// </remarks>
internal sealed class ToolCallGate(
    IPermissionService permissions,
    IEventBus eventBus,
    ILogger logger,
    IApprovalCoordinator? coordinator,
    IToolRetryDecider? retryDecider,
    IHookRunner? hooks)
{
    /// <summary>
    ///     Publish token for the TERMINAL event of a tool call (#401). Mirrors
    ///     <see cref="ToolDispatcher" /> and <see cref="ToolRetryPolicy" />: an
    ///     already-cancelled token must not gate the terminal record of a call
    ///     that a hook refused.
    /// </summary>
    private static readonly CancellationToken TerminalEventToken = CancellationToken.None;

    private readonly ToolRetryPolicy _retryPolicy = new(eventBus, permissions, logger, coordinator, retryDecider);

    /// <summary>
    ///     Outcome of the PreToolUse hook gate: the call to execute (possibly
    ///     with hook-edited args) or a refusal entry that is already reported.
    /// </summary>
    internal sealed record HookGate(ToolCallPart Call, ToolResultEntry? Refusal);

    /// <summary>
    ///     Validate tool arguments on the Result railway (#1515).
    ///     Forwarder over <see cref="ToolRetryPolicy" /> (#1507, #95 slice: verbatim move).
    /// </summary>
    internal Task<UnitResult<ToolResult>> ValidateArgumentsAsync(
        ITool tool,
        ToolCallPart toolCall,
        Activity? activity,
        CancellationToken ct) =>
        _retryPolicy.ValidateArgumentsAsync(tool, toolCall, activity, ct);

    /// <summary>
    ///     Check permission for one tool call on the Result railway (#1515).
    ///     Forwarder over <see cref="ToolRetryPolicy" /> (#1507, #95 slice: verbatim move).
    /// </summary>
    internal Task<UnitResult<ToolResult>> CheckPermissionAsync(
        ToolCallPart toolCall,
        AgentDefinition agent,
        CancellationToken effectiveCt,
        CancellationToken ct,
        Activity? activity) =>
        _retryPolicy.CheckPermissionAsync(toolCall, agent, effectiveCt, ct, activity);

    /// <summary>
    ///     Execute one tool call with the commit barrier and bounded retry (#49 PR2/PR3, #43).
    ///     Forwarder over <see cref="ToolRetryPolicy" /> (#1507, #95 slice: verbatim move).
    /// </summary>
    internal Task<ToolResultEntry> ExecuteWithRetryAsync(
        ITool tool,
        ToolCallPart toolCall,
        ToolContext ctx,
        long commitScope,
        ToolRetryPolicy.AttemptCount attempts,
        Activity? activity,
        CancellationToken effectiveCt,
        CancellationToken ct) =>
        _retryPolicy.ExecuteWithRetryAsync(tool, toolCall, ctx, commitScope, attempts, activity, effectiveCt, ct);

    /// <summary>
    ///     Run user PreToolUse hooks for one tool call. Deny (including the
    ///     runner's fail-closed deny on hook failure) and a declined ask
    ///     publish the end event and return a refusal entry; an accepted ask
    ///     and allow proceed, carrying any hook-edited args.
    /// </summary>
    internal async Task<HookGate> ApplyPreToolHooksAsync(
        ToolCallPart toolCall,
        string sessionId,
        CancellationToken effectiveCt,
        CancellationToken ct,
        Activity? activity)
    {
        HookVerdict verdict;
        try
        {
            verdict = await hooks!.RunPreToolUseAsync(
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
    /// <remarks>
    ///     #1760: a hook refusal is its own code
    ///     (<see cref="ToolErrorCodes.HookDenied" />) and not the permission
    ///     policy's — the two are decided by different parties and a user
    ///     reacts to them differently.
    /// </remarks>
    private async Task<HookGate> RefuseHook(ToolCallPart toolCall, string reason, Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Hook denied");
        ToolDispatcherLog.ToolRefusedByHook(logger, toolCall.ToolName, toolCall.Id, reason);
        var refused = ToolResult.Failed(
            reason,
            ToolErrorCodes.Create(ToolErrorCodes.HookDenied, reason));
        await eventBus.PublishAsync(new ToolExecutionEndEvent(
            toolCall.Id, refused, true), TerminalEventToken).ConfigureAwait(false);
        return new HookGate(toolCall, ToolResultEntry.From(toolCall.Id, toolCall.ToolName, refused));
    }
}
