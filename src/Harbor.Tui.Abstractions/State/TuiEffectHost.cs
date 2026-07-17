using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions.State;

namespace Harbor.Tui.Abstractions.State;

/// <summary>
///     Default <see cref="ITuiEffectRunner" />. The ONLY place that touches
///     <see cref="IAgent" /> / the slash handler. Renderers stay free of
///     <c>Harbor.Core</c> references and instead emit <see cref="TuiEffect" />.
/// </summary>
/// <remarks>
///     <para>
///         Effects run on the thread pool. Prompt/abort feed agent events back into
///         the supplied <see cref="UiStore" /> so the UI state stays in sync without
///         the renderer polling <c>IAgent.State</c>.
///     </para>
/// </remarks>
public sealed class TuiEffectHost : ITuiEffectRunner
{
    private static readonly ImmutableArray<string> SlashCommands = ImmutableArray.Create(
        "/help", "/exit", "/setup", "/auth", "/model", "/agent", "/config",
        "/providers", "/sessions", "/tui", "/storage", "/clear");

    private static readonly ImmutableHashSet<string> ExitWords =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "exit", "quit", ":q");

    private readonly IAgent _agent;
    private readonly Func<string, Task>? _slash;
    private readonly UiStore _store;
    private readonly CancellationToken _appCt;

    public TuiEffectHost(IAgent agent, UiStore store, Func<string, Task>? slash = null, CancellationToken appCt = default)
    {
        _agent = agent;
        _store = store;
        _slash = slash;
        _appCt = appCt;
    }

    public void Run(TuiEffect effect)
    {
        switch (effect)
        {
            case TuiEffect.None:
                break;
            case TuiEffect.PromptAgent p:
                _ = PromptAsync(p.Text);
                break;
            case TuiEffect.RunSlash s:
                _ = RunSlashAsync(s.Command);
                break;
            case TuiEffect.AbortAgent:
                _ = AbortAsync();
                break;
            case TuiEffect.QuitApp:
                _store.Transition(s => s with { ShouldQuit = true });
                break;
        }
    }

    /// <summary>Slash command list for input autocomplete.</summary>
    public static ImmutableArray<string> KnownSlashCommands => SlashCommands;

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

    /// <summary>Map a submitted input line to an effect (prompt / slash / quit).</summary>
    public static TuiEffect ToEffect(string text)
    {
        var trimmed = text.Trim();
        if (ExitWords.Contains(trimmed))
            return new TuiEffect.QuitApp();
        if (trimmed.StartsWith('/'))
            return new TuiEffect.RunSlash(trimmed);
        return new TuiEffect.PromptAgent(trimmed);
    }
}
