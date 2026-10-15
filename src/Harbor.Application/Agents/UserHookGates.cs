using Harbor.Application.Hooks;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     User-hook gates extracted from <see cref="AgentLoop" /> (#1101, slice 2):
///     the run's <c>SessionEnd</c> / <c>UserPromptSubmit</c> / <c>SessionStart</c>
///     points. Verbatim move — the loop's three private helpers, unchanged.
/// </summary>
/// <remarks>
///     Shares the loop's logger so log categories stay identical to pre-extraction.
/// </remarks>
internal sealed class UserHookGates(
    IHookRunner? hooks,
    ILogger logger)
{
    /// <summary>
    ///     Run user SessionEnd hooks after the terminal event. Advisory: the
    ///     runner logs hook failures and never throws on its own (except on a
    ///     cancelled token, which cannot happen here — the token is
    ///     <see cref="CancellationToken.None" /> and the run is already over),
    ///     so a hook can neither fail the run nor rewrite its outcome.
    /// </summary>
    internal async Task RunSessionEndHooksAsync(string sessionId)
    {
        if (hooks is null)
        {
            return;
        }

        await hooks.RunSessionEndAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Run the <c>UserPromptSubmit</c> gate for the prompt that opened
    ///     this run. Returns <see cref="HookVerdict.Allow" /> — which is what
    ///     every hooks-free and no-prompt path costs — or the hook's verdict.
    /// </summary>
    /// <remarks>
    ///     The prompt is read as "the last message in the session is a
    ///     <see cref="UserMessage" />", which is exactly the shape
    ///     <c>DefaultAgent.PromptAsync</c> produces: it persists the message,
    ///     then loads the context and calls this loop. A run opened without a
    ///     fresh user message (an internal re-entry, a drained background
    ///     completion) has no prompt to submit and fires no hook — claiming a
    ///     stale prompt would send the hook text the user never typed this
    ///     turn.
    /// </remarks>
    internal async Task<HookVerdict> RunUserPromptHooksAsync(
        ISessionContext session, AgentDefinition agent, ModelInfo model, CancellationToken ct)
    {
        if (hooks is null)
        {
            return HookVerdict.Allow;
        }

        IReadOnlyList<AgentMessage> messages = session.Messages;
        if (messages.Count == 0 || messages[^1] is not UserMessage prompt)
        {
            return HookVerdict.Allow;
        }

        try
        {
            return await hooks.RunUserPromptSubmitAsync(
                session.Session.Id, prompt.Content, agent.Name.Value, model.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The runner is contracted never to throw on its own; a failure
            // here is a hooks-subsystem failure, which fails closed to deny
            // exactly like the permission-subsystem failure (§G3) rather than
            // silently letting the prompt through.
            AgentLoopLog.UserPromptSubmitHooksFailed(logger, ex, session.Session.Id);
            return new HookVerdict(HookDecision.Deny, $"Hook execution failed: {ex.Message}", null);
        }
    }

    /// <summary>
    ///     Run the advisory <c>SessionStart</c> hooks once, after the terminal
    ///     decision on the prompt gate. Log-only by contract: a hook here
    ///     cannot fail the run it just watched begin.
    /// </summary>
    /// <remarks>
    ///     Deliberately rides the run token, where the post-terminal
    ///     <see cref="RunSessionEndHooksAsync" /> cannot: this event fires
    ///     mid-run, so a user who cancels must not be held for up to 30s per
    ///     hook afterwards. A cancelled run therefore simply never reports a
    ///     SessionStart it was not given the time to run.
    /// </remarks>
    internal async Task RunSessionStartHooksAsync(
        string sessionId, string agent, string model, CancellationToken ct)
    {
        if (hooks is null)
        {
            return;
        }

        await hooks.RunSessionStartAsync(sessionId, agent, model, ct).ConfigureAwait(false);
    }
}
