using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Registration;

/// <summary>
///     An <see cref="IPluginLoadHost" /> that remembers what the real host did with
///     every registration: the refusals, and — since #1737 — the acceptances.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IPluginLoadHost" /> is a contract that answers in
///         <c>Result</c>: <c>Success</c> means the registration took effect and is
///         observable, <c>Failure</c> means it was dropped and says why. A caller
///         that only sees the registrar's own answer cannot tell those apart
///         unless the refusals are kept.
///     </para>
///     <para>
///         This is a DECORATOR, not a replacement: every door forwards to the
///         real host and returns the host's own <c>Result</c> unchanged, so the
///         plugin sees the truth. What the decorator adds is the MEMORY of the
///         refusals, and — since #1737 — the LEDGER of the acceptances.
///     </para>
///     <para>
///         The ledger is what makes a run reversible. Every registration the host
///         ACCEPTED is remembered together with the way to take it back, so a
///         stop can undo exactly the set the plugin created, in the reverse order
///         the plugin created it. A registration the host REFUSED was never
///         recorded, so there is nothing to undo for it — and a failed run leaves
///         no residue behind.
///     </para>
///     <para>
///         The decorator does not log: each caller of a door already logs its own
///         refusal (the registry-builder adapters, the panel adapter, the
///         backend adapters, and the TUI branch of the registrar). Logging twice
///         would make the human-facing line ambiguous about which layer spoke.
///     </para>
/// </remarks>
internal sealed class RecordingPluginLoadHost : IPluginLoadHost
{
    private readonly IPluginLoadHost _inner;
    private readonly IReversiblePluginLoadHost? _innerReversible;
    private readonly List<string> _refusals = [];
    private readonly List<AcceptedRegistration> _accepted = [];

    /// <summary>Wrap a host and start recording its refusals and its acceptances.</summary>
    /// <param name="inner">The real host.</param>
    internal RecordingPluginLoadHost(IPluginLoadHost inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _innerReversible = inner as IReversiblePluginLoadHost;
    }

    /// <inheritdoc />
    public IServiceCollection Services => _inner.Services;

    /// <inheritdoc />
    public IConfiguration Configuration => _inner.Configuration;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory => _inner.LoggerFactory;

    /// <inheritdoc />
    public IEventBus EventBus => _inner.EventBus;

    /// <inheritdoc />
    public Result RegisterTool(ITool tool) => Record(
        "tool",
        tool.Name.Value,
        _inner.RegisterTool(tool),
        () => UndoTool(tool.Name));

    /// <inheritdoc />
    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory) => Record(
        "provider",
        providerId.Value,
        _inner.RegisterProvider(providerId, factory),
        () => UndoProvider(providerId));

    /// <inheritdoc />
    public Result RegisterAgent(AgentDefinition agent) => Record(
        "agent",
        agent.Name.Value,
        _inner.RegisterAgent(agent),
        () => UndoAgent(agent.Name));

    /// <inheritdoc />
    public Result RegisterTuiPlugin(ITuiPlugin plugin) => Record(
        "TUI plugin",
        plugin.Name,
        _inner.RegisterTuiPlugin(plugin),
        () => UndoTuiPlugin(plugin.Name));

    /// <inheritdoc />
    public Result RegisterPanelProvider(IPanelProvider panel) => Record(
        "panel",
        panel.Id,
        _inner.RegisterPanelProvider(panel),
        () => UndoPanelProvider(panel.Id));

    /// <inheritdoc />
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory) => Record(
        "session store",
        backendId,
        _inner.RegisterSessionStore(backendId, factory),
        () => UndoSessionStore(backendId));

    /// <inheritdoc />
    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory) => Record(
        "TUI backend",
        backendId,
        _inner.RegisterTuiBackend(backendId, aliases, factory),
        () => UndoTuiBackend(backendId));

    /// <summary>
    ///     The plugin's registration outcome: success when every door took
    ///     effect, failure naming each refused registration when one did not.
    /// </summary>
    internal Result ToResult() => _refusals.Count == 0
        ? Result.Success()
        : Result.Failure(
            "Plugin registration was rejected by the host: " + string.Join("; ", _refusals));

    /// <summary>
    ///     Take back every registration the host accepted, newest first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The reverse order is the order the plugin created them in, undone:
    ///         a plugin that registered a panel and then a tool gets the tool
    ///         taken back first. It costs nothing and it is the order a reader
    ///         expects from a stack.
    ///     </para>
    ///     <para>
    ///         A host that cannot undo is not silently pretended away: the
    ///         rollback fails with the reason, and the caller says so out loud.
    ///     </para>
    /// </remarks>
    internal Result Rollback()
    {
        if (_accepted.Count == 0)
        {
            return Result.Success();
        }

        var failures = new List<string>();
        for (int i = _accepted.Count - 1; i >= 0; i--)
        {
            AcceptedRegistration entry = _accepted[i];
            Result undone = entry.Undo();
            if (undone.IsFailure)
            {
                failures.Add($"{entry.Kind} '{entry.Id}': {undone.Error}");
            }
        }

        _accepted.Clear();

        return failures.Count == 0
            ? Result.Success()
            : Result.Failure(
                "The host could not take back what the plugin registered: " + string.Join("; ", failures));
    }

    /// <summary>
    ///     Pass the host's answer through unchanged, and remember either the
    ///     refusal or the acceptance.
    /// </summary>
    private Result Record(string kind, string id, Result result, Func<Result> undo)
    {
        if (result.IsFailure)
        {
            _refusals.Add($"{kind} '{id}': {result.Error}");
            return result;
        }

        _accepted.Add(new AcceptedRegistration(kind, id, undo));
        return result;
    }

    private Result UndoTool(ToolName name) => Reversible()
        ? _innerReversible!.UnregisterTool(name)
        : CannotUndo("tool", name.Value);

    private Result UndoProvider(ProviderId providerId) => Reversible()
        ? _innerReversible!.UnregisterProvider(providerId)
        : CannotUndo("provider", providerId.Value);

    private Result UndoAgent(AgentName name) => Reversible()
        ? _innerReversible!.UnregisterAgent(name)
        : CannotUndo("agent", name.Value);

    private Result UndoTuiPlugin(string name) => Reversible()
        ? _innerReversible!.UnregisterTuiPlugin(name)
        : CannotUndo("TUI plugin", name);

    private Result UndoPanelProvider(string panelId) => Reversible()
        ? _innerReversible!.UnregisterPanelProvider(panelId)
        : CannotUndo("panel", panelId);

    private Result UndoSessionStore(string backendId) => Reversible()
        ? _innerReversible!.UnregisterSessionStore(backendId)
        : CannotUndo("session store", backendId);

    private Result UndoTuiBackend(string backendId) => Reversible()
        ? _innerReversible!.UnregisterTuiBackend(backendId)
        : CannotUndo("TUI backend", backendId);

    private bool Reversible() => _innerReversible is not null;

    private static Result CannotUndo(string kind, string id) => Result.Failure(
        $"the host does not implement {nameof(IReversiblePluginLoadHost)}, so the {kind} '{id}' cannot be taken back");

    /// <summary>
    ///     One accepted registration and the way to take it back.
    /// </summary>
    private sealed record AcceptedRegistration(string Kind, string Id, Func<Result> Undo);
}
