using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Registries.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     A load host that REFUSES every door with a reason. It is the shape the
///     out-of-process MCP host has for everything but tools (#1758).
/// </summary>
/// <remarks>
///     <para>
///         This is the fixture for "what happens when a host says no". Before
///         #1758 the registrar logged such a refusal and then handed the plugin
///         back as <c>Success</c>, so the caller could not tell a fully-wired
///         plugin from one whose provider (or agent, or panel, or store) had
///         been dropped on the floor.
///     </para>
/// </remarks>
public sealed class RefusingPluginLoadHost : IPluginLoadHost
{
    /// <summary>The reason every door refuses.</summary>
    public const string Reason = "This host exposes tools only; the registration was refused.";

    /// <inheritdoc />
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <inheritdoc />
    public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;

    /// <inheritdoc />
    public IEventBus EventBus { get; } = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

    /// <inheritdoc />
    public Result RegisterTool(ITool tool) => Result.Failure(Reason);

    /// <inheritdoc />
    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory) => Result.Failure(Reason);

    /// <inheritdoc />
    public Result RegisterAgent(AgentDefinition agent) => Result.Failure(Reason);

    /// <inheritdoc />
    public Result RegisterTuiPlugin(ITuiPlugin plugin) => Result.Failure(Reason);

    /// <inheritdoc />
    public Result RegisterPanelProvider(IPanelProvider panel) => Result.Failure(Reason);

    /// <inheritdoc />
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory) => Result.Failure(Reason);

    /// <inheritdoc />
    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory) =>
        Result.Failure(Reason);
}
