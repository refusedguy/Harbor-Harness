using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.State;
/// <summary>
///     Default <see cref="ITuiEffectRunner" />. The ONLY place that touches
///     <see cref="IAgentRunner" /> / the slash handler. Renderers stay free of
///     <c>Harbor.Core</c> references and instead emit <see cref="TuiEffect" />.
/// </summary>
/// <remarks>
///     <para>
///         Effects run on the thread pool. Prompt/abort feed agent events back into
///         the supplied <see cref="UiStore" /> so the UI state stays in sync without
///         the renderer polling <c>IAgent.State</c>.
///     </para>
///     <para>
///         <b>Layering (§ARCH-002):</b> depends on <see cref="IAgentRunner" /> (the
///         minimal runner surface in <c>Harbor.Abstractions</c>), not the full
///         <see cref="IAgent" />. This keeps <c>Harbor.Terminal.Abstractions</c> in the
///         Domain layer — it never needs to reference <c>Harbor.Core</c> for agent
///         types. <see cref="IAgent" /> extends <see cref="IAgentRunner" />, so the
///         Composition Root (<c>HostBuilder</c>) can pass a concrete <c>DefaultAgent</c>
///         wherever an <c>IAgentRunner</c> is required.
///     </para>
/// </remarks>
public sealed class TuiEffectHost : ITuiEffectRunner
{
    private readonly IAgentRunner _agent;
    private readonly CancellationToken _appCt;
    private readonly ILogger<TuiEffectHost>? _logger;
    private readonly Func<string, Task>? _slash;
    private UiStore _store;

    public TuiEffectHost(
        IAgentRunner agent,
        UiStore store,
        Func<string, Task>? slash = null,
        CancellationToken appCt = default,
        ILogger<TuiEffectHost>? logger = null)
    {
        _agent = agent;
        _store = store;
        _slash = slash;
        _appCt = appCt;
        _logger = logger;
    }

    public void RebindStore(UiStore newStore)
    {
        ArgumentNullException.ThrowIfNull(newStore);
        _store = newStore;
    }

    /// <summary>Slash command list for input autocomplete.</summary>
    public static ImmutableArray<string> KnownSlashCommands => ChatCommands.Slash;

    public void Run(TuiEffect effect)
    {
        // §FP-006 (RESOLVED): each fire-and-forget async branch now attaches a
        // ContinueWith(OnlyOnFaulted) continuation that logs the exception. The
        // Run contract stays synchronous (per ITuiEffectRunner.Run), so we still
        // do NOT await — but unobserved-task exceptions are now surfaced via
        // _logger instead of dying in TaskScheduler.UnobservedTaskException.
        // Run synchronously is fine because the continuation just logs.
        //
        // Architecture audit v2 §3.4 (CT-002 RESOLVED): the inner async methods
        // (PromptAsync, RunSlashAsync, AbortAsync) catch OperationCanceledException
        // FIRST and route it to a clean "idle" transition instead of letting the
        // generic Exception catch set the status to "error". An Esc-abort is no
        // longer misreported as a failure.
        switch (effect)
        {
            case TuiEffect.None:
                break;
            case TuiEffect.PromptAgent p:
                PromptAsync(p.Text).ContinueWith(
                    t => _logger?.LogError(t.Exception, "PromptAsync failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
                break;
            case TuiEffect.RunSlash s:
                RunSlashAsync(s.Command).ContinueWith(
                    t => _logger?.LogError(t.Exception, "RunSlashAsync failed for {Command}", s.Command),
                    TaskContinuationOptions.OnlyOnFaulted);
                break;
            case TuiEffect.AbortAgent:
                AbortAsync().ContinueWith(
                    t => _logger?.LogError(t.Exception, "AbortAsync failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
                break;
            case TuiEffect.QuitApp:
                _store.Dispatch(new UiMsg.Quit());
                break;
        }
    }


    private async Task PromptAsync(string text)
    {
        _store.Dispatch(new UiMsg.AgentStarted());
        try
        {
            var result = await _agent.PromptAsync(text, _appCt).ConfigureAwait(false);

            // CRITICAL: check Result — if failure, set status to "error" and
            // reset IsAgentRunning so the UI doesn't hang in "thinking" forever.
            if (result.IsFailure)
            {
                _logger?.LogError("Agent failed: {Error}", result.Error);
                _store.Dispatch(new UiMsg.AgentEnded("error", result.Error));
            }
        }
        catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
        {
            // Architecture audit v2 §3.4 (CT-002 RESOLVED): an Esc-abort
            // cancels _appCt, which surfaces as OperationCanceledException
            // from _agent.PromptAsync. The previous generic Exception
            // catch treated this as an error and set the status bar to
            // "error". The user just wanted to abort — route to a clean
            // "idle" transition instead.
            _store.Dispatch(new UiMsg.StatusChanged("idle"));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PromptAsync failed");
            _store.Dispatch(new UiMsg.AgentEnded("error"));
        }
        finally
        {
            // Ensure IsAgentRunning is always reset — even on success path
            // where the agent loop might not have published AgentEndEvent yet.
            _store.Dispatch(new UiMsg.AgentEnded());
        }
    }

    private async Task RunSlashAsync(string command)
    {
        if (_slash is null)
        {
            _store.Dispatch(new UiMsg.AppendLine(ChatRole.Error, $"no handler for {command}"));
            return;
        }

        try
        {
            await _slash(command).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
        {
            // §3.4: same rationale as PromptAsync — abort ≠ error.
            _store.Dispatch(new UiMsg.StatusChanged("idle"));
        }
        catch (Exception ex)
        {
            _store.Dispatch(new UiMsg.AppendLine(ChatRole.Error, ex.Message));
        }
    }

    private async Task AbortAsync()
    {
        _agent.AbortSource.Cancel();
        try
        {
            // Bound the wait so a misbehaving agent loop can't hang the UI
            // forever — 5s is generous (the agent's only safe boundaries are
            // between turns / during streaming awaits, both sub-second). The
            // session manager uses a 3s bound on its own switches; here we
            // give the user-initiated Stop a slightly longer fuse.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _agent.WaitForIdleAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_appCt.IsCancellationRequested)
        {
            // Expected when the abort token fires; the agent is idle now.
            // §3.4: do not propagate as an error.
        }
        catch (Exception ex)
        {
            _store.Dispatch(new UiMsg.AppendLine(ChatRole.Error, ex.Message));
        }

        // Recreate the abort source so the next PromptAsync call observes a
        // live, un-cancelled token. Without this, a single Stop click would
        // permanently disable sending — every subsequent prompt would observe
        // IsCancellationRequested=true and immediately throw
        // OperationCanceledException.
        _agent.ResetAbortSource();

        _store.Dispatch(new UiMsg.AgentEnded("idle"));
    }
}
