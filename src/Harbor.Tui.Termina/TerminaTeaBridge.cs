using System.Collections.Concurrent;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Termina.Handlers;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.Termina;
/// <summary>
///     TEA bridge for the Termina renderer. Wraps <see cref="UiStore" /> +
///     <see cref="TuiEffectHost" /> + <see cref="KeyHandler" /> and exposes the
///     same surface area the legacy <see cref="ChatBridge" /> offered (Push,
///     PushLine, Submit) but routes everything through the shared reducer so
///     there is no duplicate state. The legacy ChatBridge remains as a
///     backward-compat facade over this type.
/// </summary>
public sealed class TerminaTeaBridge : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<string> _toastQueue = new();

    public TerminaTeaBridge(IAgent agent, Func<string, Task>? slash, ILogger logger,
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
        Store.Transition(s => s.AddLine(ChatRole.System, text));

    /// <summary>Enqueue a toast (auto-dismissed by the renderer after 4s).</summary>
    public void Toast(string message) => _toastQueue.Enqueue(message);

    /// <summary>Dequeue one pending toast, or null if none.</summary>
    public string? DequeueToast() =>
        _toastQueue.TryDequeue(out string? msg) ? msg : null;

    /// <summary>Submit a prompt through the TEA reducer (runs PromptAgent effect).</summary>
    public void Submit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var effect = Store.Dispatch(new UiMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
        Effects.Run(effect);
    }

    /// <summary>Process a key — returns the effect the host should run.</summary>
    public TuiEffect HandleKey(ConsoleKeyInfo info) => Keys.Handle(info);
}
