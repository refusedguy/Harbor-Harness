using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Wpf;
using Harbor.App.Wpf.Configuration;
using Harbor.App.Wpf.Services;
using Harbor.App.Wpf.ViewModels;
using Harbor.App.Wpf.Views;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Wpf.Tests;

/// <summary>
///     DI registration tests for <see cref="App.BuildHostInternal"/>.
///     WPF's composition root is built lazily — App.OnStartup calls
///     BuildHost() (private) which delegates to BuildHostInternal() (internal).
///     The internal hook is exposed via InternalsVisibleTo so this test can
///     call it directly without instantiating the WPF Application lifetime.
/// </summary>
/// <remarks>
///     <b>Platform:</b> WPF requires Windows. These tests run on
///     net10.0-windows only. On Linux/macOS CI they are skipped via the
///     target framework mismatch (the test project won't even restore).
/// </remarks>
public class AppDiTests
{
    private static readonly Lazy<IHost> _hostLazy = new(() => App.BuildHostInternal());

    private static IServiceProvider Services => _hostLazy.Value.Services;

    private static IHost Host => _hostLazy.Value;

    [After(HookType.Class)]
    public static async ValueTask DisposeHostAsync()
    {
        if (_hostLazy.IsValueCreated)
        {
            try
            {
                await Host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
            Host.Dispose();
        }
    }

    // ── Core Harbor services ──────────────────────────────────────────────

    [Test]
    public async Task BuildHost_Registers_IProviderRegistry()
        => await Assert.That(Services.GetService<IProviderRegistry>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_IAgentRegistry()
        => await Assert.That(Services.GetService<IAgentRegistry>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_IToolRegistry()
        => await Assert.That(Services.GetService<IToolRegistry>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_IEventBus()
        => await Assert.That(Services.GetService<IEventBus>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_IPermissionService()
        => await Assert.That(Services.GetService<IPermissionService>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_MessageConverter()
        => await Assert.That(Services.GetService<MessageConverter>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ITokenEstimator()
        => await Assert.That(Services.GetService<ITokenEstimator>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ISessionStore()
        => await Assert.That(Services.GetService<ISessionStore>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ISystemPromptBuilder()
        => await Assert.That(Services.GetService<ISystemPromptBuilder>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ICompactionService()
        => await Assert.That(Services.GetService<ICompactionService>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_IAgentLoop()
        => await Assert.That(Services.GetService<IAgentLoop>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_IAgent()
        => await Assert.That(Services.GetService<IAgent>()).IsNotNull();

    // ── App-local services ────────────────────────────────────────────────

    [Test]
    public async Task BuildHost_Registers_ThemeService()
        => await Assert.That(Services.GetService<ThemeService>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_WpfFilePicker()
        => await Assert.That(Services.GetService<WpfFilePicker>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_DialogService()
        => await Assert.That(Services.GetService<DialogService>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_WpfDispatcherAdapter()
        => await Assert.That(Services.GetService<WpfDispatcherAdapter>()).IsNotNull();

    // ── Per-app config (~/.harbor/wpf.json) ───────────────────────────────

    [Test]
    public async Task BuildHost_Registers_IAppConfigStore_WpfConfig()
        => await Assert.That(Services.GetService<IAppConfigStore<WpfConfig>>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_WpfConfig()
    {
        var config = Services.GetService<WpfConfig>();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.AppId).IsEqualTo("wpf");
        await Assert.That(config.ConfigFileName).IsEqualTo("wpf.json");
    }

    // ── ViewModels ────────────────────────────────────────────────────────

    [Test]
    public async Task BuildHost_Registers_MainViewModel()
        => await Assert.That(Services.GetService<MainViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ChatViewModel()
        => await Assert.That(Services.GetService<ChatViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_SessionListViewModel()
        => await Assert.That(Services.GetService<SessionListViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ProviderBrowserViewModel()
        => await Assert.That(Services.GetService<ProviderBrowserViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_SettingsViewModel()
        => await Assert.That(Services.GetService<SettingsViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_CodeEditorViewModel()
        => await Assert.That(Services.GetService<CodeEditorViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_DiffViewModel()
        => await Assert.That(Services.GetService<DiffViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_TokenUsageViewModel()
        => await Assert.That(Services.GetService<TokenUsageViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_CommandPaletteViewModel()
        => await Assert.That(Services.GetService<CommandPaletteViewModel>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ToastNotificationViewModel()
        => await Assert.That(Services.GetService<ToastNotificationViewModel>()).IsNotNull();

    // ── Views ─────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildHost_Registers_MainWindow()
        => await Assert.That(Services.GetService<MainWindow>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_ProviderBrowserView()
        => await Assert.That(Services.GetService<ProviderBrowserView>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_SettingsView()
        => await Assert.That(Services.GetService<SettingsView>()).IsNotNull();

    [Test]
    public async Task BuildHost_Registers_CommandPaletteView()
        => await Assert.That(Services.GetService<CommandPaletteView>()).IsNotNull();

    // ── Aggregate ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Aggregate: resolves every service declared with [Exposes(typeof(T))]
    ///     on App.BuildHostInternal in one shot.
    /// </summary>
    [Test]
    public async Task BuildHost_AllDeclaredServices_Resolvable()
    {
        var sp = Services;

        var required = new[]
        {
            typeof(IProviderRegistry),
            typeof(IAgentRegistry),
            typeof(IToolRegistry),
            typeof(IEventBus),
            typeof(IPermissionService),
            typeof(MessageConverter),
            typeof(ITokenEstimator),
            typeof(ISessionStore),
            typeof(ISystemPromptBuilder),
            typeof(ICompactionService),
            typeof(IAgentLoop),
            typeof(IAgent),
            typeof(ThemeService),
            typeof(WpfFilePicker),
            typeof(DialogService),
            typeof(WpfDispatcherAdapter),
            typeof(IAppConfigStore<WpfConfig>),
            typeof(WpfConfig),
            typeof(MainViewModel),
            typeof(ChatViewModel),
            typeof(SessionListViewModel),
            typeof(ProviderBrowserViewModel),
            typeof(SettingsViewModel),
            typeof(CodeEditorViewModel),
            typeof(DiffViewModel),
            typeof(TokenUsageViewModel),
            typeof(CommandPaletteViewModel),
            typeof(ToastNotificationViewModel),
            typeof(MainWindow),
            typeof(ProviderBrowserView),
            typeof(SettingsView),
            typeof(CommandPaletteView),
        };

        var missing = new List<Type>();
        foreach (var t in required)
        {
            if (sp.GetService(t) is null)
            {
                missing.Add(t);
            }
        }

        await Assert.That(missing).IsEmpty();
    }
}
