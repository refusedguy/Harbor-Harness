using Harbor.App.Cli.Configuration;
using Harbor.App.Cli.Demo;
using Harbor.Application.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Hosting;
using Harbor.Telemetry;
using Harbor.Ui.Framework.Projection;
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

        // KILLER_FEATURES §2.7 Feature 10 (issue #23 slice 2, #384): the shared
        // skill-freshness snapshot. Seeded once at CLI startup, re-seeded on
        // `/skills refresh`, re-resolved on `/skills update`. Painted as the
        // default-on aggregate pill in the CellForge status line; the per-skill
        // detail panel stays host opt-in (HARBOR_SKILL_FRESHNESS=1). Host-owned
        // — never a renderer builtin, so the Alt+1..9 slot order stays pinned.
        builder.Services.AddSingleton<SkillFreshnessModel>();

        // CE-4: второй путь рендера. Регистрации ленивые — резолв только
        // когда интерактивный REPL выбрал CellForge; legacy-путь не меняется.
        builder.Services.AddCellForge(TryReadCellForgeUi());

        // `harbor demo` (HARBOR_DEMO=1, set by DemoCommand): override the
        // fail-closed default asker with the scripted auto-approving gate.
        // Last IPermissionService registration wins (same trick as AddCellForge).
        if (Environment.GetEnvironmentVariable("HARBOR_DEMO") is "1")
        {
            builder.Services.AddDemoRuntime();
        }

        var host = builder.Build();

        // TaskTool holds a DeferredSubAgentRunner forwarder (built eagerly in
        // RegistriesModule, before ISessionStore/IAgentLoop exist). Its Attach
        // fires only on DI resolution — which no agent-loop path ever triggers
        // (only the /task-run slash command resolves it). Without this warmup
        // every task call in every session fails CanSpawn with the misleading
        // "sub-agents cannot invoke" error on a detached forwarder.
        _ = host.Services.GetRequiredService<Harbor.Abstractions.Agents.ISubAgentRunner>();

        return host;
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

    /// <summary>
    ///     CLI preset (di-design §3.3): jsonl storage + scrollback, and
    ///     <b>no event-bus middleware</b>.
    /// </summary>
    /// <remarks>
    ///     #478: this preset used to register
    ///     <c>new TypeFilterMiddleware(lf.CreateLogger&lt;TypeFilterMiddleware&gt;())</c>
    ///     — with no allowed types. That is not "filter everything", it is a filter
    ///     that admits every event while still declaring
    ///     <c>EventBusSinkKind.Mandatory</c>,
    ///     which <c>InMemoryEventBus</c> reads once in its constructor and which
    ///     therefore kept the CLI bus off its fast path on every publish. The
    ///     registration bought the mandatory-sink cost and returned nothing, and
    ///     its doc-comment promised configuration-driven filtering that no config
    ///     key ever fed. Removing it changes no event delivery: the filter passed
    ///     everything before, and nothing is registered now. The class stays, and
    ///     its constructor now rejects a typeless allowlist, so re-adding it means
    ///     naming the event types it is meant to admit —
    ///     <c>tests/Harbor.Architecture.Tests/TypeFilterRegistrationTests.cs</c>
    ///     fails the build if a product call site forgets.
    /// </remarks>
    private static HarborComposeOptions CliOptions(
        string harborDir,
        CliConfig cliConfig,
        Microsoft.Extensions.Configuration.IConfiguration configuration) => new()
    {
        HarborDir = harborDir,
        DefaultStorageBackend = "jsonl",
        EventBusScrollback = 1000,
        // #47/S2: export the event-bus queue-age percentiles to telemetry and
        // to the per-run log (harbor logs --last) every 30s.
        EventBusQueueAgeReportInterval = EventBusQueueAgeReporter.DefaultReportInterval,
        // #478: no EventBusMiddlewares — see the CliOptions remarks. The filter
        // registered here carried no allowed types and admitted every event, so
        // dropping it is delivery-neutral; what it stops is a Mandatory sink that
        // filtered nothing keeping the CLI off the fast path.
        DefaultTuiRenderer = cliConfig.DefaultTuiRenderer,
        RuntimeSwappable = cliConfig.RuntimeSwappable,
        Configuration = configuration,
        BootstrapLoggerFactory = () => _loggerFactory,
    };

    /// <summary>Create ~/.harbor and its session/cache subdirectories.</summary>
    private static string EnsureHarborLayout()
    {
        string harborDir = HarborPaths.GetHarborHome();
        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(Path.Combine(harborDir, "sessions"));
        Directory.CreateDirectory(Path.Combine(harborDir, "cache"));
        return harborDir;
    }
}
