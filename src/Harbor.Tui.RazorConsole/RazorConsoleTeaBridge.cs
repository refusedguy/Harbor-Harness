using System.Collections.Concurrent;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.RazorConsole.Handlers;
using Harbor.Ui.Framework.State;
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
    private readonly ConcurrentQueue<string> _toastQueue = new();
    private readonly TuiEffectHost _effects;
    private readonly KeyHandler _keys;
    private readonly ILogger _logger;
    private readonly UiStore _store;

    public RazorConsoleTeaBridge(IAgent agent, Func<string, Task>? slash, ILogger logger,
        CancellationToken appCt = default)
    {
        _logger = logger;
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, slash, appCt, null);
        _keys = new KeyHandler(_store, logger);
        _store.BindSession(agent.State.Agent.Model, agent.State.Agent.ProviderId, agent.State.Agent.Name.Value);
    }

    /// <summary>The single source of truth for the UI.</summary>
    public UiStore Store => _store;

    /// <summary>Effect runner — call after <see cref="KeyHandler.Handle" /> returns non-None.</summary>
    public TuiEffectHost Effects => _effects;

    /// <summary>Key handler that turns ConsoleKeyInfo into UiMsg dispatches.</summary>
    public KeyHandler Keys => _keys;

    /// <summary>Dispatch an agent event into the store (data path).</summary>
    public void Push(AgentEvent @event) => _store.Dispatch(@event);

    /// <summary>Dispatch a synthesized system line.</summary>
    public void PushLine(string text) =>
        _store.Transition(s => s.AddLine(ChatRole.System, text));

    /// <summary>Enqueue a toast (auto-dismissed by the renderer after 4s).</summary>
    public void Toast(string message) => _toastQueue.Enqueue(message);

    /// <summary>Dequeue one pending toast, or null if none.</summary>
    public string? DequeueToast() =>
        _toastQueue.TryDequeue(out var msg) ? msg : null;

    /// <summary>Submit a prompt through the TEA reducer (runs PromptAgent effect).</summary>
    public void Submit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var effect = _store.Dispatch(new UiMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
        _effects.Run(effect);
    }

    /// <summary>Process a key — returns the effect the host should run.</summary>
    public TuiEffect HandleKey(ConsoleKeyInfo info) => _keys.Handle(info);

    /// <inheritdoc />
    public void Dispose()
    {
        // UiStore / TuiEffectHost have no disposable resources.
    }
}
