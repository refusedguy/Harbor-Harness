using Harbor.Abstractions.Events;
using Harbor.App.Cli.Configuration;
using Harbor.Registries.Events;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A3 (DI analyzers): one deliberate temporary provider for the bootstrap
// logger factory (the Avalonia CreateBootstrapLoggerFactory pattern) — the
// CliConfig eager load must route warnings through the configured providers.
#pragma warning disable DI014, DI016

namespace Harbor.App.Cli.Hosting;

/// <summary>
///     Фасад совместимости: все существующие точки вызова Build не меняются.
///     Весь DI-граф собирается одним вызовом Registration.AddHarbor (§7.2).
/// </summary>
internal static partial class HostBuilder
{
    private static ILoggerFactory _loggerFactory = null!;
    private static ILogger _logger = null!;

    public static IHost Build(params string[] args)
    {
        string harborDir = EnsureHarborLayout();
        var builder = Host.CreateApplicationBuilder();
        ConfigureLogging(builder, args);

        _loggerFactory = builder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        _logger = _loggerFactory.CreateLogger(typeof(HostBuilder).FullName ?? "HostBuilder");
        _logger.LogInformation("Building host");

        builder.Services.AddCliConfiguration(_loggerFactory, out var cliConfig);

        // Весь граф — один вызов. Специфика CLI выражена пресетом (§3.3).
        builder.Services.AddHarbor(CliOptions(harborDir, cliConfig, builder.Configuration));

        builder.Services.AddCliCompositeConfig();

        return builder.Build();
    }

    /// <summary>CLI preset (di-design §3.3): jsonl storage, scrollback + TypeFilter middleware.</summary>
    private static HarborComposeOptions CliOptions(
        string harborDir,
        CliConfig cliConfig,
        Microsoft.Extensions.Configuration.IConfiguration configuration) => new()
    {
        HarborDir = harborDir,
        DefaultStorageBackend = "jsonl",
        EventBusScrollback = 1000,
        EventBusMiddlewares = lf =>
            new IEventBusMiddleware[] { new TypeFilterMiddleware(lf.CreateLogger<TypeFilterMiddleware>()) },
        DefaultTuiRenderer = cliConfig.DefaultTuiRenderer,
        Configuration = configuration,
        BootstrapLoggerFactory = () => _loggerFactory,
    };

    /// <summary>Create ~/.harbor and its session/cache subdirectories.</summary>
    private static string EnsureHarborLayout()
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(homeDir, ".harbor");
        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(Path.Combine(harborDir, "sessions"));
        Directory.CreateDirectory(Path.Combine(harborDir, "cache"));
        return harborDir;
    }
}
