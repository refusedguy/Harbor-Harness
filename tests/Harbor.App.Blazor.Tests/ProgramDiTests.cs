using Harbor.App.Blazor;
using Harbor.App.Blazor.Configuration;
using Harbor.App.Blazor.Services;
using Harbor.App.Blazor.ViewModels;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Storage.Memory;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Blazor.Tests;

/// <summary>
///     DI registration tests for <see cref="Program.BuildApp"/>.
///     Exercises the Blazor composition root without starting Kestrel — the
///     internal BuildApp hook returns the built WebApplication whose
///     <see cref="Microsoft.AspNetCore.Builder.WebApplication.Services"/>
///     ServiceProvider the tests query.
/// </summary>
public class ProgramDiTests
{
    private static readonly Lazy<Microsoft.AspNetCore.Builder.WebApplication> _appLazy = new(() =>
        Program.BuildApp(Array.Empty<string>()));

    private static IServiceProvider Services => _appLazy.Value.Services;

    [After(HookType.Class)]
    public static async ValueTask DisposeAppAsync()
    {
        if (_appLazy.IsValueCreated)
        {
            await _appLazy.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Harbor framework services ─────────────────────────────────────────

    [Test]
    public async Task BuildApp_Registers_UiStore()
        => await Assert.That(Services.GetService<UiStore>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_MemorySessionStore()
        => await Assert.That(Services.GetService<MemorySessionStore>()).IsNotNull();

    // ── App-local UI services ─────────────────────────────────────────────

    [Test]
    public async Task BuildApp_Registers_BlazorDispatcherAdapter()
        => await Assert.That(Services.GetService<BlazorDispatcherAdapter>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_ThemeService()
        => await Assert.That(Services.GetService<ThemeService>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_DialogService()
        => await Assert.That(Services.GetService<DialogService>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_ToastService()
        => await Assert.That(Services.GetService<ToastService>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_CommandPaletteService()
        => await Assert.That(Services.GetService<CommandPaletteService>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_SessionBrowserService()
        => await Assert.That(Services.GetService<SessionBrowserService>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_ProviderBrowserService()
        => await Assert.That(Services.GetService<ProviderBrowserService>()).IsNotNull();

    // ── Per-app config (~/.harbor/blazor.json) ────────────────────────────

    [Test]
    public async Task BuildApp_Registers_IAppConfigStore_BlazorConfig()
        => await Assert.That(Services.GetService<IAppConfigStore<BlazorConfig>>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_BlazorConfig()
    {
        var config = Services.GetService<BlazorConfig>();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.AppId).IsEqualTo("blazor");
        await Assert.That(config.ConfigFileName).IsEqualTo("blazor.json");
    }

    // ── Shared common config (~/.harbor/config.json) ──────────────────────

    [Test]
    public async Task BuildApp_Registers_ICommonConfigStore()
        => await Assert.That(Services.GetService<ICommonConfigStore>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_CommonConfig()
    {
        var config = Services.GetService<CommonConfig>();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.ConfigFileName).IsEqualTo("config.json");
        await Assert.That(config.DefaultProvider).IsEqualTo("anthropic");
    }

    [Test]
    public async Task BuildApp_Registers_CompositeConfig_BlazorConfig()
    {
        var composite = Services.GetService<CompositeConfig<BlazorConfig>>();
        await Assert.That(composite).IsNotNull();
        await Assert.That(composite!.AppId).IsEqualTo("blazor");
    }

    // ── ViewModels ────────────────────────────────────────────────────────

    [Test]
    public async Task BuildApp_Registers_ChatViewModel()
        => await Assert.That(Services.GetService<ChatViewModel>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_SessionListViewModel()
        => await Assert.That(Services.GetService<SessionListViewModel>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_ProviderBrowserViewModel()
        => await Assert.That(Services.GetService<ProviderBrowserViewModel>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_SettingsViewModel()
        => await Assert.That(Services.GetService<SettingsViewModel>()).IsNotNull();

    [Test]
    public async Task BuildApp_Registers_TokenUsageViewModel()
        => await Assert.That(Services.GetService<TokenUsageViewModel>()).IsNotNull();

    // ── Aggregate ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Aggregate: resolves every service declared with [Exposes(typeof(T))]
    ///     on Program.Main (which mirrors Program.BuildApp) in one shot.
    /// </summary>
    [Test]
    public async Task BuildApp_AllDeclaredServices_Resolvable()
    {
        var sp = Services;

        var required = new[]
        {
            typeof(UiStore),
            typeof(MemorySessionStore),
            typeof(BlazorDispatcherAdapter),
            typeof(ThemeService),
            typeof(DialogService),
            typeof(ToastService),
            typeof(CommandPaletteService),
            typeof(SessionBrowserService),
            typeof(ProviderBrowserService),
            typeof(IAppConfigStore<BlazorConfig>),
            typeof(BlazorConfig),
            typeof(ICommonConfigStore),
            typeof(CommonConfig),
            typeof(CompositeConfig<BlazorConfig>),
            typeof(ChatViewModel),
            typeof(SessionListViewModel),
            typeof(ProviderBrowserViewModel),
            typeof(SettingsViewModel),
            typeof(TokenUsageViewModel),
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
