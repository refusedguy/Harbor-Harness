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

    internal void Freeze()
    {
        Tools.Freeze();
        Providers.Freeze();
    }
}
