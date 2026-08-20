using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Core.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Cli.Configuration;
using Harbor.Cli.Logging;
using Harbor.Core.Agents;
using Harbor.Core.Resilience;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Ipc.Server;
using Harbor.Providers.Ollama;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Terminal.Abstractions;
using Harbor.Tools.Builtin;
using Harbor.Tools.Mcp;
using Harbor.Tui.Plain;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
#endif
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Storage.Sqlite;
#endif
#if HARBOR_WITH_SPECTRE_TUI
using Harbor.Tui.Ansi;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.Spectre;
using Harbor.Tui.Spectre.Fullscreen;
using Harbor.Tui.Termina;
using Harbor.Tui.TerminalGui;
#endif
// A3 (DI analyzers) added Excubo.Analyzers.DependencyInjection rules
// DI014 (BuildServiceProvider should be disposed) and DI016 (don't call
// BuildServiceProvider during composition). The HostBuilder pattern
// constructs a temporary ServiceProvider deliberately so the eagerly
// constructed ToolRegistry/ProviderRegistry/AgentRegistry see the same
// ILoggerFactory / IEventBus that the final ServiceProvider will use —
// this is the documented pattern from sub-agent 1 (Plugins.Runtime) and
// is preserved until a full async-HostBuilder refactor lands.
#pragma warning disable DI014, DI016
#if HARBOR_WITH_PLUGINS
using Excubo.Analyzers.DependencyInjection;
#endif
namespace Harbor.Cli.Hosting;

/// <summary>
///     DI host configuration — single responsibility: wire services.
///     Extracted from Program.cs to reduce god object.
/// </summary>
internal static partial class HostBuilder
{
    private static ILoggerFactory _loggerFactory = null!;
    private static ILogger _logger = null!;

    /// <summary>
    ///     Runtime probe: returns true if the named assembly has been loaded
    ///     into the current AppDomain. Used as defense-in-depth to skip
    ///     service registration for optional features whose ProjectReference
    ///     has been excluded at build time (via the HarborWith* MSBuild flags).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the runtime safety net on top of the compile-time
    ///         <c>#if HARBOR_WITH_*</c> guards. It catches the case where a
    ///         plugin/scripting assembly is referenced but never loaded (e.g.
    ///         if the type isn't directly used during startup, the CLR doesn't
    ///         eagerly load its assembly).
    ///     </para>
    /// </remarks>
    private static bool IsAssemblyLoaded(string name) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

    // Each [Exposes(typeof(T))] declaration below is enforced by
    // Excubo.Analyzers.DependencyInjectionValidation (rules EDI01–EDI04) and
    // exercised by Harbor.App.Cli.Tests/HostBuilderDiTests.cs which builds the
    // host and asserts every [Exposes] type is resolvable from the resulting
    // IServiceProvider. Keep this list in sync with the actual services.AddXxx
    // calls in RegisterCore / RegisterRegistries / RegisterStorage / RegisterTui.
    //
    // Note: the [Exposes] attributes are emitted only when Excubo is
    // referenced (i.e. when at least one optional ProjectReference that
    // brings it transitively is included — currently Harbor.Plugins.*).
    // The DI tests still resolve these services in all build variants.
#if HARBOR_WITH_PLUGINS
    [Exposes(typeof(IConfigStore))]
    [Exposes(typeof(AuthStore))]
    [Exposes(typeof(OnboardingWizard))]
    [Exposes(typeof(ITokenTracker))]
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(MessageConverter))]
    [Exposes(typeof(IAgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IMcpRegistry))]
    [Exposes(typeof(PanelRegistry))]
    [Exposes(typeof(IPanelRegistry))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ITuiRenderer))]
    [Exposes(typeof(IAppConfigStore<CliConfig>))]
    [Exposes(typeof(CliConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<CliConfig>))]
#endif
    public static IHost Build(params string[] args)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(homeDir, ".harbor");
        string sessionsDir = Path.Combine(harborDir, "sessions");
        string cacheDir = Path.Combine(harborDir, "cache");
        string sqlitePath = Path.Combine(harborDir, "sessions.db");

        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(sessionsDir);
        Directory.CreateDirectory(cacheDir);

        var builder = Host.CreateApplicationBuilder();
        ConfigureLogging(builder, args);

        _loggerFactory = builder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        _logger = _loggerFactory.CreateLogger(typeof(HostBuilder).FullName ?? "HostBuilder");

        _logger.LogInformation("Building host");
        _logger.LogInformation("Feature flags: plugins={Plugins}, scripting={Scripting}, " +
                               "spectre-tui={SpectreTui}, all-providers={AllProviders}",
            IsAssemblyLoaded("Harbor.Plugins.Runtime"),
            IsAssemblyLoaded("Harbor.Scripting.Hosting"),
            IsAssemblyLoaded("Harbor.Tui.Spectre"),
            IsAssemblyLoaded("Harbor.Providers.Anthropic"));

        RegisterCore(builder);
        RegisterHttpClients(builder);
        RegisterRegistries(builder, harborDir);
        RegisterStorage(builder, sessionsDir, sqlitePath);
        RegisterTui(builder);
        RegisterIpc(builder);
        return builder.Build();
    }
}
