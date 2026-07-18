using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Storage.Memory;
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
        builder.Services.AddSingleton<IPermissionService, PermissionService>();
        builder.Services.AddSingleton<ISessionStore, MemorySessionStore>();
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        builder.Services.AddSingleton<ICompactionService, CompactionService>();
        builder.Services.AddSingleton<AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();
        builder.Services.AddLogging();

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        return builder.Build();
    }
}
