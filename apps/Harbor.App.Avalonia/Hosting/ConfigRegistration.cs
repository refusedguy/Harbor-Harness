using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Ui.Framework.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     Configuration registration — eagerly loads
///     <see cref="CommonConfig" /> (<c>~/.harbor/config.json</c>) and
///     <see cref="AvaloniaConfig" /> (<c>~/.harbor/avalonia.json</c>) using
///     a bootstrap logger factory, then registers the loaded configs +
///     their stores + the <see cref="IAuthResolver" /> /
///     <see cref="IModelCatalog" /> / <see cref="CompositeConfig{T}" /> as
///     singletons on the DI container.
/// </summary>
/// <remarks>
///     <para>
///         We load the configs eagerly using a *bootstrap* logger factory
///         so we can register the loaded <see cref="AvaloniaConfig" /> as a
///         singleton before the host is built. The previous pattern called
///         <c>BuildServiceProvider()</c> twice just to get an ILogger — that
///         creates a parallel DI container that disposes out from under us
///         and is flagged by the .NET analyser as an anti-pattern.
///     </para>
///     <para>
///         The bootstrap logger factory is intentionally leaked to process
///         lifetime (same effective lifetime as the previous tempSp pattern)
///         because its loggers are passed to long-lived singletons
///         (ToolRegistry, ProviderRegistry, InMemoryMcpRegistry) constructed
///         eagerly by other registration classes.
///     </para>
/// </remarks>
internal static class ConfigRegistration
{
    /// <summary>
    ///     Load the persisted configs from disk and register them + their
    ///     stores + the auth resolver / model catalog / composite on the
    ///     DI container. Returns the loaded configs + the auth resolver +
    ///     the model catalog so the caller can pass them to the eager
    ///     registry builders.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="bootstrapLoggerFactory">Bootstrap logger factory (must outlive the host build).</param>
    /// <param name="harborDir">The ~/.harbor directory path.</param>
    /// <returns>The loaded configs + auth resolver + model catalog.</returns>
    public static async Task<ConfigBundle> RegisterAsync(
        IServiceCollection services,
        ILoggerFactory bootstrapLoggerFactory,
        string harborDir)
    {
        // ── Per-app Avalonia configuration (~/.harbor/avalonia.json) ──
        // Non-overlapping with CLI/WPF/MAUI/Blazor config files AND with the
        // shared ~/.harbor/config.json. JsonAppConfigStore handles atomic
        // write (temp + rename) + SemaphoreSlim thread safety.
        var bootstrapConfigLogger = bootstrapLoggerFactory.CreateLogger<JsonAppConfigStore<AvaloniaConfig>>();
        var configStore = new JsonAppConfigStore<AvaloniaConfig>(
            new AvaloniaConfig(),
            bootstrapConfigLogger);
        services.AddSingleton<IAppConfigStore<AvaloniaConfig>>(sp =>
            new JsonAppConfigStore<AvaloniaConfig>(
                new AvaloniaConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<AvaloniaConfig>>>()));
        var avaloniaConfigResult = await configStore.LoadAsync().ConfigureAwait(false);
        var avaloniaConfig = avaloniaConfigResult.IsSuccess
            ? avaloniaConfigResult.Value
            : new AvaloniaConfig();
        services.AddSingleton(avaloniaConfig);

        // ── Shared common configuration (~/.harbor/config.json) ──
        // CommonConfig holds API keys, default provider/model, storage backend,
        // log level, permissions, plugins, network, compaction — every field
        // that is shared across ALL Harbor apps. Loaded eagerly so the
        // Avalonia composition root can read StorageBackend / LogLevel / etc.
        // synchronously. Same atomic-write + thread-safe pattern as
        // JsonAppConfigStore<T>.
        var bootstrapCommonLogger = bootstrapLoggerFactory.CreateLogger<JsonCommonConfigStore>();
        var commonStore = new JsonCommonConfigStore(
            new CommonConfig(),
            bootstrapCommonLogger);
        var commonConfigResult = await commonStore.LoadAsync().ConfigureAwait(false);
        var commonConfig = commonConfigResult.IsSuccess
            ? commonConfigResult.Value
            : new CommonConfig();
        services.AddSingleton<ICommonConfigStore>(sp =>
            new JsonCommonConfigStore(
                new CommonConfig(),
                sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
        services.AddSingleton(commonConfig);

        // Bridge ICommonConfigStore (Desktop.Abstractions) to
        // ICommonConfigReader (Ui.Framework). Without this adapter,
        // SessionFactory (in Ui.Framework) can't read the persisted
        // provider/model because Ui.Framework can't reference Desktop.Abstractions
        // (circular dependency via Terminal.Abstractions).
        services.AddSingleton<ICommonConfigReader>(sp => new CommonConfigReaderAdapter(sp));

        // ── Auth resolver + model catalog for OpenAI-compatible providers ──
        // The wizard persists API keys to CommonConfig.ApiKeys. This resolver
        // reads them (falling back to env vars like KILO_API_KEY) so providers
        // registered from providers/*.json can authenticate. The model catalog
        // fetches + caches the /models endpoint per provider.
        var authResolver = new CommonConfigAuthResolver(
            commonStore,
            bootstrapLoggerFactory.CreateLogger<CommonConfigAuthResolver>());
        var modelCatalog = new DynamicModelCatalog(
            new HttpClient(),
            Path.Combine(harborDir, "cache", "providers"),
            bootstrapLoggerFactory.CreateLogger<DynamicModelCatalog>());

        // Register the auth resolver as a singleton so view-models
        // (ProviderModelPickerViewModel, SettingsViewModel) can resolve
        // IAuthResolver through DI — same path the agent uses at request time.
        services.AddSingleton<IAuthResolver>(authResolver);

        // ── Composite: CommonConfig + AvaloniaConfig ──
        services.AddSingleton<CompositeConfig<AvaloniaConfig>>(sp =>
            new CompositeConfig<AvaloniaConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<AvaloniaConfig>()));

        return new ConfigBundle(commonConfig, avaloniaConfig, authResolver, modelCatalog);
    }
}

/// <summary>
///     Bundle of eagerly-loaded config singletons + auth/model dependencies
///     returned by <see cref="ConfigRegistration.RegisterAsync" /> so the
///     caller can pass them to the eager registry builders
///     (<see cref="ProviderRegistration" />, <see cref="AgentRegistration" />,
///     <see cref="StorageRegistration" />).
/// </summary>
internal sealed record ConfigBundle(
    CommonConfig CommonConfig,
    AvaloniaConfig AvaloniaConfig,
    IAuthResolver AuthResolver,
    IModelCatalog ModelCatalog);
