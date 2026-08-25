using Harbor.Application.Configuration;
using Harbor.Core.Events;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

// DI014/DI016: config is loaded synchronously at composition time by explicit
// construction — no temporary BuildServiceProvider anywhere in Hosting.
#pragma warning disable RS0030

internal static class ConfigurationModule
{
    /// <summary>
    ///     Loads CommonConfig and HarborConfig exactly once into the context,
    ///     registers the config stores, constructs the event bus explicitly
    ///     (middlewares from options), and applies HARBOR_MODEL env override.
    /// </summary>
    internal static HarborCompositionContext AddHarborConfiguration(
        this IServiceCollection services,
        HarborComposeOptions options)
    {
        var loggerFactory = options.BootstrapLoggerFactory?.Invoke()
            ?? LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var ctx = new HarborCompositionContext(options, loggerFactory);

        // ---- stores -------------------------------------------------------
        services.AddSingleton<IConfigStore>(sp => new JsonConfigStore(
            logger: sp.GetRequiredService<ILogger<JsonConfigStore>>()));
        if (options.RegisterCommonConfigStore)
        {
            services.AddSingleton<ICommonConfigStore>(sp => new JsonCommonConfigStore(
                new CommonConfig { ConfigDirectory = options.HarborDir },
                sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
        }

        // ---- eager loads: exactly one read per file ------------------------
        // Scoped to options.HarborDir (NOT the process-wide ~/HarborHome) so
        // embedded hosts and tests get a hermetic config graph.
        var commonStore = new JsonCommonConfigStore(
            new CommonConfig { ConfigDirectory = options.HarborDir },
            loggerFactory.CreateLogger<JsonCommonConfigStore>());
        var commonResult = commonStore.LoadAsync().GetAwaiter().GetResult();
        if (commonResult.IsFailure)
            ctx.Logger.LogWarning("Failed to load CommonConfig, using defaults: {Error}", commonResult.Error);
        ctx.Common = commonResult.IsSuccess ? commonResult.Value : new CommonConfig();

        var harborStore = new JsonConfigStore(
            ctx.Options.ConfigPath,
            loggerFactory.CreateLogger<JsonConfigStore>());
        var harborResult = harborStore.LoadAsync().GetAwaiter().GetResult();
        if (harborResult.IsSuccess)
        {
            ctx.Harbor = harborResult.Value;
        }
        else
        {
            ctx.Logger.LogWarning("Failed to load HarborConfig, using defaults: {Error}", harborResult.Error);
            ctx.Harbor = new HarborConfig();
        }

        services.AddSingleton(ctx.Common);

        string? envModel = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrEmpty(envModel))
            ctx.Harbor.Model = envModel;

        // App hook (desktop overrides ctx.Common with its async-loaded instance).
        options.AfterConfiguration?.Invoke(ctx);

        services.AddSingleton(ctx.Common);

        // ---- event bus: constructed explicitly, registered as instance -----
        var middlewares = options.EventBusMiddlewares?.Invoke(loggerFactory) ?? Array.Empty<IEventBusMiddleware>();
        var eventBusLogger = loggerFactory.CreateLogger<InMemoryEventBus>();
        ctx.EventBus = options.EventBusScrollback is { } scrollback
            ? new InMemoryEventBus(eventBusLogger, maxScrollback: scrollback, middlewares.ToArray())
            : new InMemoryEventBus(eventBusLogger);

        return ctx;
    }
}
