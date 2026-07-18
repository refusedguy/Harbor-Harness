using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     DI registration tests for <see cref="AppHost.BuildAsync"/>.
///     Mirrors Harbor.App.Cli.Tests/HostBuilderDiTests.cs but exercises the
///     Avalonia composition root (which wires a subset of services — no MCP,
///     no plugins, no Jsonl providers).
/// </summary>
public class AppHostDiTests
{
    private static readonly Lazy<Task<IHost>> _hostLazy = new(() => AppHost.BuildAsync(Array.Empty<string>()));

    private static async Task<IHost> GetHostAsync() => await _hostLazy.Value;

    private static IServiceProvider Services => _hostLazy.Value.IsCompletedSuccessfully
        ? _hostLazy.Value.Result.Services
        : throw new InvalidOperationException("Host not yet built");

    [After(HookType.Class)]
    public static async ValueTask DisposeHostAsync()
    {
        if (_hostLazy.IsValueCreated && _hostLazy.Value.IsCompletedSuccessfully)
        {
            var host = _hostLazy.Value.Result;
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
            host.Dispose();
        }
    }

    // ── Core services ─────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_Registers_ITokenEstimator()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ITokenEstimator>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IEventBus()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IEventBus>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_ISystemPromptBuilder()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ISystemPromptBuilder>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_MessageConverter()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<MessageConverter>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IAgentLoop()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IAgentLoop>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IAgent()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IAgent>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_ISessionStore()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ISessionStore>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_ICompactionService()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ICompactionService>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IPermissionService()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IPermissionService>()).IsNotNull();
    }

    // ── Registries ────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_Registers_IToolRegistry()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IToolRegistry>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IProviderRegistry()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IProviderRegistry>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IAgentRegistry()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IAgentRegistry>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_IMcpRegistry()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<IMcpRegistry>()).IsNotNull();
    }

    // ── UI framework + app-local services ─────────────────────────────────

    [Test]
    public async Task BuildAsync_Registers_UiStore()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<UiStore>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_TuiEffectHost()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<TuiEffectHost>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_ThemeService()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ThemeService>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_DialogService()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<DialogService>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_AvaloniaFilePicker()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<AvaloniaFilePicker>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_SessionManager()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<SessionManager>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_ToastService()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ToastService>()).IsNotNull();
    }

    // ── ViewModels ────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_Registers_MainViewModel()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<MainViewModel>()).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_Registers_ChatViewModel()
    {
        await GetHostAsync();
        await Assert.That(Services.GetService<ChatViewModel>()).IsNotNull();
    }

    // ── Aggregate ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Aggregate: resolves every service declared with [Exposes(typeof(T))]
    ///     on AppHost.BuildAsync in one shot.
    /// </summary>
    [Test]
    public async Task BuildAsync_AllDeclaredServices_Resolvable()
    {
        await GetHostAsync();
        var sp = Services;

        var required = new[]
        {
            typeof(ITokenEstimator),
            typeof(IEventBus),
            typeof(ISystemPromptBuilder),
            typeof(MessageConverter),
            typeof(IAgentLoop),
            typeof(IAgent),
            typeof(ISessionStore),
            typeof(ICompactionService),
            typeof(IPermissionService),
            typeof(IToolRegistry),
            typeof(IProviderRegistry),
            typeof(IAgentRegistry),
            typeof(IMcpRegistry),
            typeof(UiStore),
            typeof(TuiEffectHost),
            typeof(ThemeService),
            typeof(DialogService),
            typeof(AvaloniaFilePicker),
            typeof(SessionManager),
            typeof(ToastService),
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
