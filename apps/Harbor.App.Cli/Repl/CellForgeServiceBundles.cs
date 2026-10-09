using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Hosting.Rendering;
using Harbor.Registries.Agents;
using Harbor.Registries.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     The always-present container services a CellForge run needs (issue #486
///     finding 3): configuration, registries, bus, agent, the slash adapter the
///     composition root built, and a logger. Eight members — the most a
///     bundle carries under the issue's own done-state — so inserting one more
///     service here rebinds only this bundle's call sites, never the runner's.
/// </summary>
internal sealed record CellForgeCoreServices(
    IConfigStore ConfigStore,
    IProviderRegistry ProviderRegistry,
    IAgentRegistry AgentRegistry,
    AuthStore AuthStore,
    IEventBus EventBus,
    IAgent Agent,
    LegacySlashRunner LegacySlash,
    ILogger Logger);

/// <summary>
///     The nullable host-provided integrations (issue #486 finding 3): every
///     member may be absent — test doubles and hosts without the registration
///     pass null, which is the documented degradation each consumer already
///     implements rather than a second wiring.
/// </summary>
internal sealed record CellForgeOptionalServices(
    ISessionStore? SessionStore,
    IRendererPipeline? RendererPipeline,
    ITokenTracker? Tokens,
    Harbor.Hosting.PluginReloadService? PluginReload,
    IProviderHealthCheck? HealthCheck,
    Harbor.Ui.Framework.Panels.IPanelRegistry? PanelRegistry,
    Harbor.Application.Diagnostics.DiagnosticsAggregator? DiagnosticsAggregator,
    // #857: the worktree column of the /jump palette. Null in test doubles and
    // in a host that registered no query — the command then lists sessions
    // only, the degradation the jump panel already documents.
    Harbor.Abstractions.Git.IGitQuery? GitQuery);
