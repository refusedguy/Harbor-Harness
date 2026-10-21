// PluginInventoryLoadHost.cs — the decorator that remembers what every door
// did, so the inventory can say what a plugin actually registered (#1722).
//
// WHY A DECORATOR AND NOT A CHANGE TO THE DOORS
// ---------------------------------------------
// `IPluginLoadHost` is the contract the registrar writes into. Its doors
// answer with a `Result`: `Success` means the contribution landed and is
// observable, `Failure` means it was refused and says why (#1758). That
// answer is the ONLY signal the caller gets — and it says nothing about
// WHICH contribution was registered, or under which name.
//
// The inventory needs that detail, and it must not be a second copy of the
// registration logic: a table that says "the tool axis registers tools" is a
// thing that can drift from what the host does.
//
// So the decorator WRAPS the host. Every door forwards to the real host and
// returns the host's own `Result` unchanged — the plugin sees the truth, and
// the host's behaviour is exactly what it was. What the decorator adds is the
// MEMORY: the door, the subject, and the answer.
//
// IT IS PER PLUGIN
// ----------------
// The host creates one of these per plugin registration, so the recorded
// entries belong to exactly one plugin. A single shared recorder would need
// a "current plugin" notion, and that is state that can be wrong.

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

namespace Harbor.Plugins.Hosting;

/// <summary>
///     An <see cref="IPluginLoadHost" /> that records what every door did.
/// </summary>
/// <remarks>
///     <para>
///         This is a DECORATOR, not a replacement: every door forwards to the
///         real host and returns the host's own <c>Result</c> unchanged, so the
///         plugin sees the truth and the host's behaviour is untouched.
///     </para>
///     <para>
///         The decorator does not log. Each caller of a door already logs its
///         own refusal, and logging twice would make the human-facing line
///         ambiguous about which layer spoke.
///     </para>
/// </remarks>
internal sealed class PluginInventoryLoadHost : IPluginLoadHost
{
    private readonly IPluginLoadHost _inner;
    private readonly List<PluginInventoryRegistration> _registrations = [];

    /// <summary>Wrap a host and start recording what its doors do.</summary>
    /// <param name="inner">The real host.</param>
    internal PluginInventoryLoadHost(IPluginLoadHost inner)
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
    public Result RegisterTool(ITool tool) =>
        Record(nameof(RegisterTool), tool.Name.Value, _inner.RegisterTool(tool));

    /// <inheritdoc />
    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory) =>
        Record(nameof(RegisterProvider), providerId.Value, _inner.RegisterProvider(providerId, factory));

    /// <inheritdoc />
    public Result RegisterAgent(AgentDefinition agent) =>
        Record(nameof(RegisterAgent), agent.Name.Value, _inner.RegisterAgent(agent));

    /// <inheritdoc />
    public Result RegisterTuiPlugin(ITuiPlugin plugin) =>
        Record(nameof(RegisterTuiPlugin), plugin.Name, _inner.RegisterTuiPlugin(plugin));

    /// <inheritdoc />
    public Result RegisterPanelProvider(IPanelProvider panel) =>
        Record(nameof(RegisterPanelProvider), panel.Id, _inner.RegisterPanelProvider(panel));

    /// <inheritdoc />
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory) =>
        Record(nameof(RegisterSessionStore), backendId, _inner.RegisterSessionStore(backendId, factory));

    /// <inheritdoc />
    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory) =>
        Record(nameof(RegisterTuiBackend), backendId, _inner.RegisterTuiBackend(backendId, aliases, factory));

    /// <summary>
    ///     What the doors did, in the order they were called.
    /// </summary>
    internal IReadOnlyList<PluginInventoryRegistration> Registrations
    {
        get
        {
            lock (_registrations)
            {
                return [.. _registrations];
            }
        }
    }

    /// <summary>
    ///     Pass the host's answer through unchanged, and remember it.
    /// </summary>
    /// <param name="door">The door that was called.</param>
    /// <param name="subject">What was being registered.</param>
    /// <param name="result">The host's own answer.</param>
    /// <returns>The host's answer, unchanged.</returns>
    private Result Record(string door, string subject, Result result)
    {
        PluginInventoryOutcome outcome = result.IsFailure
            ? PluginInventoryOutcome.Refused
            : PluginInventoryOutcome.Registered;

        string? reason = null;
        if (result.IsFailure)
        {
            reason = result.Error;
        }

        lock (_registrations)
        {
            _registrations.Add(new PluginInventoryRegistration(door, subject, outcome, reason));
        }

        return result;
    }
}
