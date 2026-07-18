using System.IO;
using System.Windows;
using Harbor.App.Wpf.Configuration;
using Harbor.App.Wpf.Services;
using Harbor.App.Wpf.ViewModels;
using Harbor.App.Wpf.Views;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Storage.Memory;
using Excubo.Analyzers.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Wpf;

/// <summary>
///     WPF application root. Bootstraps the Microsoft.Extensions.Hosting DI
///     container, registers Harbor.Core services, and shows the
///     <see cref="MainWindow" /> on startup.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>
    ///     Gets the active <see cref="IHost" /> for ad-hoc service resolution
    ///     from code-behind where DI injection is impractical (e.g. value
    ///     converters declared in XAML). Returns <see langword="null" /> before
    ///     <see cref="OnStartup" /> completes.
    /// </summary>
    public static IHost? HostInstance { get; private set; }

    /// <summary>
    ///     Resolves a required service from the current <see cref="HostInstance" />.
    /// </summary>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>The resolved service instance.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the host has not been built or the service is not registered.
    /// </exception>
    public static T Resolve<T>() where T : notnull
    {
        if (HostInstance is null)
            throw new InvalidOperationException("Harbor host has not been built yet.");
        return HostInstance.Services.GetRequiredService<T>();
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = BuildHost();
        HostInstance = _host;

        // Apply persisted theme before showing the window so the first paint is correct.
        var theme = _host.Services.GetRequiredService<ThemeService>();
        theme.ApplyPersistedTheme();

        await _host.StartAsync().ConfigureAwait(false);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                _host.Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }
        }
        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        return BuildHostInternal();
    }

    /// <summary>
    ///     Builds the Harbor DI host. Internal so the DI test project
    ///     (Harbor.App.Wpf.Tests) can call it directly without going through
    ///     the WPF startup lifetime. The public surface stays unchanged.
    /// </summary>
    /// <returns>A built <see cref="IHost"/>. Caller is responsible for disposal.</returns>
    // [Exposes(typeof(T))] declarations are validated by Excubo.Analyzers.DependencyInjectionValidation
    // (EDI01–EDI04) and exercised at runtime by Harbor.App.Wpf.Tests/AppDiTests.cs.
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(MessageConverter))]
    [Exposes(typeof(ITokenEstimator))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(IAgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(ThemeService))]
    [Exposes(typeof(WpfFilePicker))]
    [Exposes(typeof(DialogService))]
    [Exposes(typeof(WpfDispatcherAdapter))]
    [Exposes(typeof(IAppConfigStore<WpfConfig>))]
    [Exposes(typeof(WpfConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<WpfConfig>))]
    [Exposes(typeof(MainViewModel))]
    [Exposes(typeof(ChatViewModel))]
    [Exposes(typeof(SessionListViewModel))]
    [Exposes(typeof(ProviderBrowserViewModel))]
    [Exposes(typeof(SettingsViewModel))]
    [Exposes(typeof(CodeEditorViewModel))]
    [Exposes(typeof(DiffViewModel))]
    [Exposes(typeof(TokenUsageViewModel))]
    [Exposes(typeof(CommandPaletteViewModel))]
    [Exposes(typeof(ToastNotificationViewModel))]
    [Exposes(typeof(MainWindow))]
    [Exposes(typeof(ProviderBrowserView))]
    [Exposes(typeof(SettingsView))]
    [Exposes(typeof(CommandPaletteView))]
    internal static IHost BuildHostInternal()
    {
        var builder = Host.CreateApplicationBuilder();

        // appsettings.json is copied next to the .exe.
        string baseDir = AppContext.BaseDirectory;
        var settingsPath = Path.Combine(baseDir, "appsettings.json");
        if (File.Exists(settingsPath))
        {
            builder.Configuration.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);
        }

        ConfigureLogging(builder);
        RegisterHarbor(builder.Services);
        RegisterApp(builder.Services);
        return builder.Build();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
            o.UseUtcTimestamp = false;
        });
        builder.Logging.AddDebug();
    }

    private static void RegisterHarbor(IServiceCollection services)
    {
        // Core registries
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();

        // Event bus + permission service + message converter (required by AgentLoop)
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<IPermissionService, PermissionService>();
        services.AddSingleton<MessageConverter>();
        services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();

        // Session store — in-memory for the standalone desktop shell.
        // Swap to JsonlSessionStore / SqliteSessionStore if you want durable
        // persistence across app restarts (requires referencing those projects).
        services.AddSingleton<ISessionStore, MemorySessionStore>();

        // System prompt builder + compaction service
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        services.AddSingleton<ICompactionService, CompactionService>();

        // Agent loop (stateless per-run)
        services.AddSingleton<IAgentLoop, AgentLoop>();
        // Default agent — used when the user has not picked one explicitly.
        services.AddTransient<IAgent>(sp =>
        {
            var loop = sp.GetRequiredService<IAgentLoop>();
            var sessionStore = sp.GetRequiredService<ISessionStore>();
            var eventBus = sp.GetRequiredService<IEventBus>();
            var logger = sp.GetRequiredService<ILogger<DefaultAgent>>();
            return new DefaultAgent(sessionStore, loop, eventBus, logger);
        });

        // ── Per-app WPF configuration (~/.harbor/wpf.json) ──
        // Non-overlapping with CLI/Avalonia/MAUI/Blazor config files AND
        // with the shared ~/.harbor/config.json.
        services.AddSingleton<IAppConfigStore<WpfConfig>>(sp =>
            new JsonAppConfigStore<WpfConfig>(
                new WpfConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<WpfConfig>>>()));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IAppConfigStore<WpfConfig>>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            return result.IsSuccess ? result.Value : new WpfConfig();
        });

        // ── Shared common configuration (~/.harbor/config.json) ──
        // CommonConfig holds API keys, default provider/model, storage backend,
        // log level, permissions, plugins, network, compaction — every field
        // that is shared across ALL Harbor apps. Loaded eagerly so the WPF
        // composition root can read StorageBackend / LogLevel / etc.
        // synchronously. Same atomic-write + thread-safe pattern as
        // JsonAppConfigStore<T>.
        services.AddSingleton<ICommonConfigStore>(sp =>
            new JsonCommonConfigStore(
                new CommonConfig(),
                sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<ICommonConfigStore>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            return result.IsSuccess ? result.Value : new CommonConfig();
        });

        // ── Composite: CommonConfig + WpfConfig ──
        services.AddSingleton<CompositeConfig<WpfConfig>>(sp =>
            new CompositeConfig<WpfConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<WpfConfig>()));
    }

    private static void RegisterApp(IServiceCollection services)
    {
        // Services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<WpfFilePicker>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<WpfDispatcherAdapter>();

        // View models — transient so each window gets a fresh state.
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<SessionListViewModel>();
        services.AddTransient<ProviderBrowserViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<CodeEditorViewModel>();
        services.AddTransient<DiffViewModel>();
        services.AddTransient<TokenUsageViewModel>();
        services.AddTransient<CommandPaletteViewModel>();
        services.AddSingleton<ToastNotificationViewModel>();

        // Views — transient windows / controls.
        services.AddSingleton<MainWindow>();
        services.AddTransient<ProviderBrowserView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<CommandPaletteView>();
    }
}
