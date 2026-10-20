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
///     An <see cref="IPluginLoadHost" /> that records the registrations the real
///     host REFUSED, so the registrar can report a half-wired plugin instead of
///     a silently-dropped one (#1758).
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
///         refusals. The registrar asks it for the verdict once the plugin has
///         been wired.
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
    private readonly List<string> _refusals = [];

    /// <summary>Wrap a host and start recording its refusals.</summary>
    /// <param name="inner">The real host.</param>
    internal RecordingPluginLoadHost(IPluginLoadHost inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
    public Result RegisterTool(ITool tool) => Record("tool", tool.Name.Value, _inner.RegisterTool(tool));

    /// <inheritdoc />
    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory) =>
        Record("provider", providerId.Value, _inner.RegisterProvider(providerId, factory));

    /// <inheritdoc />
    public Result RegisterAgent(AgentDefinition agent) =>
        Record("agent", agent.Name.Value, _inner.RegisterAgent(agent));

    /// <inheritdoc />
    public Result RegisterTuiPlugin(ITuiPlugin plugin) =>
        Record("TUI plugin", plugin.Name, _inner.RegisterTuiPlugin(plugin));

    /// <inheritdoc />
    public Result RegisterPanelProvider(IPanelProvider panel) =>
        Record("panel", panel.Id, _inner.RegisterPanelProvider(panel));

    /// <inheritdoc />
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory) =>
        Record("session store", backendId, _inner.RegisterSessionStore(backendId, factory));

    /// <inheritdoc />
    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory) =>
        Record("TUI backend", backendId, _inner.RegisterTuiBackend(backendId, aliases, factory));

    /// <summary>
    ///     The plugin's registration outcome: success when every door took
    ///     effect, failure naming each refused registration when one did not.
    /// </summary>
    internal Result ToResult() => _refusals.Count == 0
        ? Result.Success()
        : Result.Failure(
            "Plugin registration was rejected by the host: " + string.Join("; ", _refusals));

    /// <summary>
    ///     Pass the host's answer through unchanged, and remember a refusal.
    /// </summary>
    private Result Record(string kind, string id, Result result)
    {
        if (result.IsFailure)
        {
            _refusals.Add($"{kind} '{id}': {result.Error}");
        }

        return result;
    }
}
