using Harbor.Abstractions.Events;
using Harbor.App.Cli.Configuration;
using Harbor.Application.Configuration;
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

        // CE-4: второй путь рендера. Регистрации ленивые — резолв только
        // когда интерактивный REPL выбрал CellForge; legacy-путь не меняется.
        builder.Services.AddCellForge(TryReadCellForgeUi());

        return builder.Build();
    }

    /// <summary>
    ///     Best-effort read of the <c>consoleEx</c> section from
    ///     <c>~/.harbor/config.json</c> for DI registration. Runs before DI /
    ///     <see cref="Harbor.Application.Configuration.IConfigStore" /> exists,
    ///     so it mirrors <see cref="TuiMode" />'s pre-host readers: missing or
    ///     unreadable file ⇒ defaults, never a throw. Manual field extraction —
    ///     no JsonSerializer reflection on the AOT path.
    /// </summary>
    private static CellForgeUiConfig TryReadCellForgeUi()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(home, ".harbor", "config.json");
            if (!File.Exists(path))
                return CellForgeUiConfig.Default;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("consoleEx", out var el)
                || el.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return CellForgeUiConfig.Default;
            }

            bool enabled = !el.TryGetProperty("enabled", out var enabledEl)
                           || enabledEl.ValueKind != System.Text.Json.JsonValueKind.False;
            bool syncUpdates = el.TryGetProperty("syncUpdates", out var syncEl)
                ? syncEl.ValueKind != System.Text.Json.JsonValueKind.False
                : CellForgeUiConfig.Default.SyncUpdates;
            return new CellForgeUiConfig(enabled, syncUpdates);
        }
        catch
        {
            // Best-effort — defaults win over any config-read failure.
            return CellForgeUiConfig.Default;
        }
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
