using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Maui.Configuration;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Storage.Memory;
using Excubo.Analyzers.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Maui;

/// <summary>
///     MAUI app entry point — wires Harbor's application services into the
///     <see cref="MauiAppBuilder"/> DI container. Mirrors the structure of
///     <c>Harbor.App.Blazor/Program.cs</c> but uses MAUI's hosting model.
/// </summary>
/// <remarks>
///     <para>
///         This is a skeleton: it registers the core Harbor services (agent
///         loop, registries, event bus, permission service, in-memory session
///         store) and returns a configured <see cref="MauiApp"/>. The UI shell
///         (App.xaml + MainPage) is the minimal boilerplate needed to launch a
///         window; the chat UI itself is left as a v0.5 follow-up.
///     </para>
///     <para>
///         <b>Why a skeleton:</b> the MAUI workload is not available on the
///         Linux CI sandbox, so this project is intentionally lightweight. The
///         csproj + MauiProgram + App.xaml triple is enough to compile on
///         Windows + macOS Catalyst once the workloads are installed.
///     </para>
/// </remarks>
public static class MauiProgram
{
    /// <summary>
    ///     Build the MAUI app, register Harbor services, and return the
    ///     configured <see cref="MauiApp"/>.
    /// </summary>
    /// <returns>A configured <see cref="MauiApp"/> instance.</returns>
    // [Exposes(typeof(T))] declarations are validated by Excubo.Analyzers.DependencyInjectionValidation
    // (EDI01–EDI04). MAUI workload is not on Linux CI so Harbor.App.Maui.Tests is a
    // skeleton project that compiles only on Windows/macOS (see PLAN.md).
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(AgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(IAppConfigStore<MauiConfig>))]
    [Exposes(typeof(MauiConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<MauiConfig>))]
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Harbor service registrations — same shape as Harbor.App.Blazor/Program.cs.
        builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
        builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
        builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
        builder.Services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ILogger<PermissionService>>(),
            workspaceRoot: Directory.GetCurrentDirectory()));
        builder.Services.AddSingleton<ISessionStore, MemorySessionStore>();
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        builder.Services.AddSingleton<ITokenTracker, TokenTracker>();
        builder.Services.AddSingleton<MessageConverter>();
        builder.Services.AddSingleton<ICompactionService, CompactionService>();
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();
        builder.Services.AddLogging();

        // ── Per-app MAUI configuration (~/.harbor/maui.json) ──
        // Non-overlapping with CLI/Avalonia/WPF/Blazor config files AND with
        // the shared ~/.harbor/config.json.
        builder.Services.AddSingleton<IAppConfigStore<MauiConfig>>(sp =>
            new JsonAppConfigStore<MauiConfig>(
                new MauiConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<MauiConfig>>>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IAppConfigStore<MauiConfig>>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            return result.IsSuccess ? result.Value : new MauiConfig();
        });

        // ── Shared common configuration (~/.harbor/config.json) ──
        // CommonConfig holds API keys, default provider/model, storage backend,
        // log level, permissions, plugins, network, compaction — every field
        // that is shared across ALL Harbor apps. Loaded eagerly so the MAUI
        // composition root can read StorageBackend / LogLevel / etc.
        // synchronously. Same atomic-write + thread-safe pattern as
        // JsonAppConfigStore<T>.
        builder.Services.AddSingleton<ICommonConfigStore>(sp =>
            new JsonCommonConfigStore(
                new CommonConfig(),
                sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<ICommonConfigStore>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            return result.IsSuccess ? result.Value : new CommonConfig();
        });

        // ── Composite: CommonConfig + MauiConfig ──
        builder.Services.AddSingleton<CompositeConfig<MauiConfig>>(sp =>
            new CompositeConfig<MauiConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<MauiConfig>()));

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        return builder.Build();
    }
}
