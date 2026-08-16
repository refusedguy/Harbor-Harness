using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Maui;
using Harbor.App.Maui.Configuration;
using Harbor.Core.Agents;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Maui.Tests;

/// <summary>
///     DI registration tests for <see cref="MauiProgram.CreateMauiApp"/>.
///     MAUI's hosting model differs from Microsoft.Extensions.Hosting in that
///     the container is exposed via <see cref="MauiApp.Services"/> (no IHost).
/// </summary>
/// <remarks>
///     <b>Platform:</b> requires the maui-windows (or maui-maccatalyst)
///     workload. On Linux CI these tests are skipped because the project
///     won't restore. They run on Windows + macOS Catalyst.
/// </remarks>
public class MauiProgramDiTests
{
    private static readonly Lazy<MauiApp> _appLazy = new(() => MauiProgram.CreateMauiApp());

    private static IServiceProvider Services => _appLazy.Value.Services;

    [After(HookType.Class)]
    public static ValueTask DisposeAppAsync()
    {
        // MauiApp doesn't implement IAsyncDisposable; the .NET MAUI host
        // manages its own lifetime. Best-effort cleanup — nothing to do here
        // since the test process exits after the test class finishes.
        return ValueTask.CompletedTask;
    }

    [Test]
    public async Task CreateMauiApp_Registers_IEventBus()
        => await Assert.That(Services.GetService<IEventBus>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_IProviderRegistry()
        => await Assert.That(Services.GetService<IProviderRegistry>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_IToolRegistry()
        => await Assert.That(Services.GetService<IToolRegistry>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_IAgentRegistry()
        => await Assert.That(Services.GetService<IAgentRegistry>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_IPermissionService()
        => await Assert.That(Services.GetService<IPermissionService>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_ISessionStore()
        => await Assert.That(Services.GetService<ISessionStore>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_ISystemPromptBuilder()
        => await Assert.That(Services.GetService<ISystemPromptBuilder>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_ICompactionService()
        => await Assert.That(Services.GetService<ICompactionService>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_AgentLoop_Concrete()
        => await Assert.That(Services.GetService<AgentLoop>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_IAgent()
        => await Assert.That(Services.GetService<IAgent>()).IsNotNull();

    // ── Per-app config (~/.harbor/maui.json) ──────────────────────────────

    [Test]
    public async Task CreateMauiApp_Registers_IAppConfigStore_MauiConfig()
        => await Assert.That(Services.GetService<IAppConfigStore<MauiConfig>>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_MauiConfig()
    {
        var config = Services.GetService<MauiConfig>();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.AppId).IsEqualTo("maui");
        await Assert.That(config.ConfigFileName).IsEqualTo("maui.json");
    }

    // ── Shared common config (~/.harbor/config.json) ──────────────────────

    [Test]
    public async Task CreateMauiApp_Registers_ICommonConfigStore()
        => await Assert.That(Services.GetService<ICommonConfigStore>()).IsNotNull();

    [Test]
    public async Task CreateMauiApp_Registers_CommonConfig()
    {
        var config = Services.GetService<CommonConfig>();
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.ConfigFileName).IsEqualTo("config.json");
        await Assert.That(config.DefaultProvider).IsEqualTo("anthropic");
    }

    [Test]
    public async Task CreateMauiApp_Registers_CompositeConfig_MauiConfig()
    {
        var composite = Services.GetService<CompositeConfig<MauiConfig>>();
        await Assert.That(composite).IsNotNull();
        await Assert.That(composite!.AppId).IsEqualTo("maui");
    }

    /// <summary>
    ///     Aggregate: resolves every service declared with [Exposes(typeof(T))]
    ///     on MauiProgram.CreateMauiApp in one shot.
    /// </summary>
    [Test]
    public async Task CreateMauiApp_AllDeclaredServices_Resolvable()
    {
        var sp = Services;

        var required = new[]
        {
            typeof(IEventBus),
            typeof(IProviderRegistry),
            typeof(IToolRegistry),
            typeof(IAgentRegistry),
            typeof(IPermissionService),
            typeof(ISessionStore),
            typeof(ISystemPromptBuilder),
            typeof(ICompactionService),
            typeof(AgentLoop),
            typeof(IAgent),
            typeof(IAppConfigStore<MauiConfig>),
            typeof(MauiConfig),
            typeof(ICommonConfigStore),
            typeof(CommonConfig),
            typeof(CompositeConfig<MauiConfig>),
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
