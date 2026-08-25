using Harbor.Cli.Configuration;
using Harbor.Application.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// RS0030: CliConfig is eager-loaded synchronously at startup — no
// SynchronizationContext, safe to block (same pattern as before the move).
#pragma warning disable RS0030

namespace Harbor.Cli.Hosting;

internal static partial class HostBuilder
{
    /// <summary>
    ///     CLI-специфика вне общего корня: приложение-специфичный конфиг cli.json.
    ///     Загружается один раз; CompositeConfig регистрируется после AddHarbor,
    ///     когда CommonConfig уже загружен хостингом.
    /// </summary>
    internal static IServiceCollection AddCliConfiguration(
        this IServiceCollection services,
        ILoggerFactory loggerFactory,
        out CliConfig cliConfig)
    {
        var store = new JsonAppConfigStore<CliConfig>(
            new CliConfig(),
            loggerFactory.CreateLogger<JsonAppConfigStore<CliConfig>>());
        services.AddSingleton<IAppConfigStore<CliConfig>>(store);

        var result = store.LoadAsync().GetAwaiter().GetResult();
        if (result.IsFailure)
        {
            _logger.LogWarning("Failed to load CliConfig, using defaults: {Error}", result.Error);
            cliConfig = new CliConfig();
        }
        else
        {
            cliConfig = result.Value;
        }
        services.AddSingleton(cliConfig);
        return services;
    }

    /// <summary>Composite config over Common + Cli — call AFTER AddHarbor.</summary>
    internal static IServiceCollection AddCliCompositeConfig(this IServiceCollection services)
    {
        services.AddSingleton<CompositeConfig<CliConfig>>(sp =>
            new CompositeConfig<CliConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<CliConfig>()));
        return services;
    }
}
