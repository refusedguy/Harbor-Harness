using Avalonia;
using Harbor.App.Avalonia;

// Harbor.App.Avalonia — standalone desktop GUI entry point.
//
// The host (Microsoft.Extensions.Hosting) is built in AppHost.BuildAsync() and the
// Avalonia desktop lifetime runs on the main thread. All Harbor services
// (IAgent, IAgentLoop, IEventBus, IToolRegistry, IProviderRegistry, ISessionStore,
// UiStore, TuiEffectHost) are resolved from the DI container by App.OnFrameworkInitializationCompleted.

// RS0030: we call BuildAsync().GetAwaiter().GetResult() in Main because Main is the
// synchronous entry point. No SynchronizationContext is installed yet, so this is safe.
#pragma warning disable RS0030

if (args is { Length: > 0 } && args[0] is "--help" or "-h")
{
    Console.WriteLine("Harbor.App.Avalonia — standalone desktop GUI for the Harbor AI coding agent.");
    Console.WriteLine();
    Console.WriteLine("Usage: dotnet run --project apps/Harbor.App.Avalonia");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  HARBOR_MODEL     provider/model (default: ollama/qwen2.5-coder:7b)");
    Console.WriteLine("  HARBOR_STORAGE   memory | jsonl (default: memory)");
    Console.WriteLine("  HARBOR_LOGLEVEL  Trace|Debug|Information|Warning|Error (default: Information)");
    return 0;
}

using var host = await AppHost.BuildAsync(args);


AppBuilder.Configure<App>()
    .UseWin32()
    .UseSkia()
    .UseHarfBuzz()
    .WithInterFont()
    .LogToTrace()
    .AfterSetup(_ =>
    {
        // Hand the built ServiceProvider + IHost to App so the ViewModels can
        // resolve services and App.OnShutdownRequested can stop the host
        // cleanly on exit (prevents the "window won't close" hang where
        // background Task.Run instances kept the process alive).
        App.Services = host.Services;
        App.Host = host;
    })
    .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnMainWindowClose);

return 0;
