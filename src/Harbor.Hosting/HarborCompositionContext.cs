using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Configuration;
using Harbor.Abstractions.Events;
using Harbor.Registries.Events;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

/// <summary>
///     Eagerly-constructed composition state shared by the AddHarbor modules.
///     Replaces the four temporary <c>BuildServiceProvider()</c> calls and the
///     static logger fields of the old CLI HostBuilder: the bootstrap logger
///     factory lives for the process lifetime, configs are loaded exactly once
///     and stored here, the event bus is constructed explicitly and published
///     as an instance, and the registries are built before being frozen.
/// </summary>
public sealed class HarborCompositionContext
{
    public HarborCompositionContext(HarborComposeOptions options, ILoggerFactory loggerFactory)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger("Harbor.Hosting");
    }

    public HarborComposeOptions Options { get; }

    /// <summary>Process-lifetime bootstrap logger factory (Avalonia pattern).</summary>
    public ILoggerFactory LoggerFactory { get; }

    public ILogger Logger { get; }

    /// <summary>Eagerly loaded common config (loaded once, §3.4 of di-design). Apps may override via AfterConfiguration.</summary>
    public CommonConfig Common { get; set; } = new();

    /// <summary>config.json → HarborConfig (model/tools overrides), env-overridden.</summary>
    public HarborConfig Harbor { get; internal set; } = new();

    /// <summary>Explicitly constructed event bus; registered as an instance.</summary>
    public IEventBus EventBus { get; internal set; } = null!;

    /// <summary>Eager registries (agents / tools / providers / panels), frozen before publication.</summary>
    public HarborRegistries Registries { get; } = new();
}

/// <summary>The eager registry bundle built by <c>AddHarborRegistries</c>.</summary>
public sealed class HarborRegistries
{
    public AgentRegistry Agents { get; internal set; } = null!;
    public ToolRegistry Tools { get; internal set; } = null!;
    public ProviderRegistry Providers { get; internal set; } = null!;
    public PanelRegistry Panels { get; internal set; } = null!;

    /// <summary>
    ///     Session-store backends a plugin registered through
    ///     <c>IPluginLoadHost.RegisterSessionStore</c> during the startup plugin load in
    ///     <c>AddHarborRegistries</c>, read by <c>StorageModule</c> when it builds the
    ///     storage registry (#581). Empty when the plugin stack is compiled out.
    /// </summary>
    /// <remarks>
    ///     Startup only, by the same contract that governs every other registration: a
    ///     hot-reload pass composes its own load host over a throwaway service
    ///     collection, so a backend it registers cannot reach the already-built storage
    ///     singleton. Known limitation, not a silent gap — the same one
    ///     <c>PluginRuntimeComposer</c> documents for late-loaded plugins.
    /// </remarks>
    public IReadOnlyDictionary<string, Func<ISessionStore>> SessionStores { get; internal set; } =
        new Dictionary<string, Func<ISessionStore>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     TUI backends a plugin registered through
    ///     <c>IPluginLoadHost.RegisterTuiBackend</c> during the startup plugin load, read
    ///     by <c>TuiModule</c> when it builds the backend registry — the same registry
    ///     the runtime-swap table is derived from, so a plugin backend is listed by
    ///     <c>/renderer</c> and can be swapped to too (#581/#584). Startup-only, like
    ///     <see cref="SessionStores" />.
    /// </summary>
    public IReadOnlyDictionary<string, PluginTuiBackend> TuiBackends { get; internal set; } =
        new Dictionary<string, PluginTuiBackend>(StringComparer.OrdinalIgnoreCase);

    internal void Freeze()
    {
        Tools.Freeze();
        Providers.Freeze();
    }
}
