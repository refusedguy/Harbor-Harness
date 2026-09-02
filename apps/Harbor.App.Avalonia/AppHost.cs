using Excubo.Analyzers.DependencyInjection;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.Services;
using Harbor.Application.Sessions;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Hosting;
using Harbor.Ipc;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia;

/// <summary>
///     Composition root for the standalone Harbor Avalonia app. The whole
///     Harbor DI-graph is one <c>Registration.AddHarbor</c> call (§7.3); the
///     app adds only its UI shell (view-models, app services) and post-build
///     event routing (<see cref="UiEventRouter" />).
/// </summary>
internal static class AppHost
{
    /// <summary>
    ///     Build the DI host. Safe to call from Main before the Avalonia lifetime starts.
    /// </summary>
    /// <param name="args">Command-line args (forwarded to <see cref="Host.CreateApplicationBuilder" />).</param>
    /// <returns>A started <see cref="IHost" />. Dispose on shutdown.</returns>
    // [Exposes(typeof(T))] declarations are validated by Excubo.Analyzers.DependencyInjectionValidation
    // (EDI01–EDI04) and exercised at runtime by Harbor.App.Avalonia.Tests/AppHostDiTests.cs.
    [Exposes(typeof(ITokenTracker))]
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(MessageConverter))]
    [Exposes(typeof(IAgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IMcpRegistry))]
    [Exposes(typeof(UiStore))]
    [Exposes(typeof(TuiEffectHost))]
    [Exposes(typeof(ThemeService))]
    [Exposes(typeof(DialogService))]
    [Exposes(typeof(AvaloniaFilePicker))]
    [Exposes(typeof(SessionManager))]
    [Exposes(typeof(GitService))]
    [Exposes(typeof(IToastService))]
    [Exposes(typeof(AvaloniaDispatcherAdapter))]
    [Exposes(typeof(IHarborClient))]
    [Exposes(typeof(IAppConfigStore<AvaloniaConfig>))]
    [Exposes(typeof(AvaloniaConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<AvaloniaConfig>))]
    public static async Task<IHost> BuildAsync(string[] args)
    {
        string harborDir = ResolveHarborDir();
        var builder = Host.CreateApplicationBuilder(args);

        LoggingConfiguration.Configure(builder);
        var loggerFactory = LoggingConfiguration.CreateBootstrapLoggerFactory();
        var config = await ConfigRegistration.RegisterAsync(builder.Services, loggerFactory, harborDir).ConfigureAwait(false);

        // Весь граф Harbor — один вызов композиционного корня (§7.3).
        builder.Services.AddHarbor(DesktopOptions(harborDir, config, loggerFactory, builder.Configuration));

        // Avalonia-специфика: UI-shell сервисы, IHarborClient и view-models.
        ServiceRegistration.RegisterAppServices(builder.Services);
        ServiceRegistration.RegisterHarborClient(builder.Services);
        ViewModelRegistration.Register(builder.Services);

        var host = builder.Build();
        UiEventRouter.Bind(host.Services);
        return host;
    }

    /// <summary>
    ///     Desktop preset (di-design §3.3 as plain data): memory storage,
    ///     desktop-safe 10-tool subset without MCP, model from CommonConfig,
    ///     config stores owned by the app's async loader (not Hosting's).
    /// </summary>
    private static HarborComposeOptions DesktopOptions(
        string harborDir,
        ConfigBundle config,
        ILoggerFactory loggerFactory,
        IConfiguration configuration) => new()
    {
        HarborDir = harborDir,
        DefaultStorageBackend = "memory",
        ToolSet = HarborToolSetKind.Standard10,
        IncludeMcpTools = false,
        ModelSource = HarborAgentModelSource.CommonConfig,
        Providers = HarborProviderFlavor.Desktop,
        DesktopAuthResolver = config.AuthResolver,
        DesktopModelCatalog = config.ModelCatalog,
        RegisterCommonConfigStore = false,
        AfterConfiguration = c => c.Common = config.CommonConfig,
        BootstrapLoggerFactory = () => loggerFactory,
        Configuration = configuration,
    };

    /// <summary>
    ///     Resolve ~/.harbor. HOME may have been re-pointed by an embedding
    ///     harness (E2E driver) AFTER .NET cached the special-folder table —
    ///     GetFolderPath then returns EMPTY. $HOME env is always authoritative
    ///     on Unix; fall back to it explicitly, and to the app base dir as a
    ///     last resort.
    /// </summary>
    private static string ResolveHarborDir()
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p
            ? p
            : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
                ? h
                : AppContext.BaseDirectory;
        string harborDir = Path.Combine(homeDir, ".harbor");
        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(Path.Combine(harborDir, "sessions"));
        return harborDir;
    }
}
