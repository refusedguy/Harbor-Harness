using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
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
    private readonly Func<string, Task>? _slash;
    private readonly ILogger<TuiEffectHost>? _logger;
    private readonly UiStore _store;

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
                _store.Transition(s => s with { ShouldQuit = true });
                break;
        }
    }


    private async Task PromptAsync(string text)
    {
        _store.Transition(s => s with { IsAgentRunning = true, Status = "running" });
        try
        {
            await _agent.PromptAsync(text, _appCt).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _store.Transition(s => s with { Status = "error" });
        }
    }

    private async Task RunSlashAsync(string command)
    {
        if (_slash is null)
        {
            _store.Transition(s => s.AddLine(ChatRole.Error, $"no handler for {command}"));
            return;
        }

        try
        {
            await _slash(command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _store.Transition(s => s.AddLine(ChatRole.Error, ex.Message));
        }
    }

    private async Task AbortAsync()
    {
        _agent.AbortSource.Cancel();
        try
        {
            await _agent.WaitForIdleAsync(_appCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the abort token fires; the agent is idle now.
        }
        catch (Exception ex)
        {
            _store.Transition(s => s.AddLine(ChatRole.Error, ex.Message));
        }

        _store.Transition(s => s with { IsAgentRunning = false, Status = "idle" });
    }
}
