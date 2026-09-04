using System.Collections.Concurrent;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.RazorConsole.Handlers;
using Harbor.Tui.RazorConsole.Views;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.RazorConsole;
/// <summary>
///     TEA bridge for the RazorConsole renderer. Wraps <see cref="UiStore" /> +
///     <see cref="TuiEffectHost" /> + <see cref="KeyHandler" /> and exposes a
///     Push / Submit / Toast surface so the legacy <c>ChatBridge</c> can route
///     events through the shared reducer instead of duplicating state.
/// </summary>
public sealed class RazorConsoleTeaBridge : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<string> _toastQueue = new();

    public RazorConsoleTeaBridge(IAgent agent, Func<string, Task>? slash, ILogger logger,
        CancellationToken appCt = default)
    {
        _logger = logger;
        Store = new UiStore();
        Effects = new TuiEffectHost(agent, Store, slash, appCt);
        Keys = new KeyHandler(Store, logger);
        Store.BindSession(agent.State.Agent.Model, agent.State.Agent.ProviderId, agent.State.Agent.Name.Value);
    }

    /// <summary>The single source of truth for the UI.</summary>
    public UiStore Store
    {
        get;
    }

    /// <summary>Effect runner — call after <see cref="KeyHandler.Handle" /> returns non-None.</summary>
    public TuiEffectHost Effects
    {
        get;
    }

    /// <summary>Key handler that turns ConsoleKeyInfo into UiMsg dispatches.</summary>
    public KeyHandler Keys
    {
        get;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // UiStore / TuiEffectHost have no disposable resources.
    }

    /// <summary>Dispatch an agent event into the store (data path).</summary>
    public void Push(AgentEvent @event) => Store.Dispatch(@event);

    /// <summary>Dispatch a synthesized system line.</summary>
    public void PushLine(string text) =>
        Store.Dispatch(new UiMsg.AppendLine(ChatRole.System, text));

    /// <summary>Enqueue a toast (auto-dismissed by the renderer after 4s).</summary>
    public void Toast(string message) => _toastQueue.Enqueue(message);

    /// <summary>Dequeue one pending toast, or null if none.</summary>
    public string? DequeueToast() =>
        _toastQueue.TryDequeue(out string? msg) ? msg : null;

    /// <summary>Shared diagnostics panel (resolved from DI by the renderer).</summary>
    public IDiagnosticsPanel? DiagnosticsPanel { get; set; }

    /// <summary>Dump recent log entries into the chat transcript via the store.</summary>
    public void DumpDiagnostics()
    {
        if (DiagnosticsPanel is null)
        {
            PushLine("/logs: no diagnostics panel registered (non-interactive build).");
            return;
        }
        var view = new DiagnosticsView();
        foreach (string line in view.Render(DiagnosticsPanel, 10))
            Store.Dispatch(new UiMsg.AppendLine(ChatRole.System, line));
    }

    /// <summary>Submit a prompt through the TEA reducer (runs PromptAgent effect).</summary>
    public void Submit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        Store.Dispatch(new UiMsg.InputText(text));
        var effect = Store.Dispatch(new UiMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
        Effects.Run(effect);
    }

    /// <summary>Process a key — returns the effect the host should run.</summary>
    public TuiEffect HandleKey(ConsoleKeyInfo info) => Keys.Handle(info);
}
