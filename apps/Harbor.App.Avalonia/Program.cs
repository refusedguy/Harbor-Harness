using Avalonia;
using Avalonia.Controls;
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
    Console.WriteLine("Usage: dotnet run --project apps/Harbor.App.Avalonia [--shell classic|orca] [--theme dark|light|system]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --shell classic|orca   Shell layout (default: classic).");
    Console.WriteLine("                         'orca' = experimental Orca-inspired Harbor shell.");
    Console.WriteLine("  --theme dark|light|system  Theme (default: dark).");
    Console.WriteLine("  --gallery                Open the HDS component gallery (dev mode).");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  HARBOR_SHELL     classic | orca (default: classic — same as --shell).");
    Console.WriteLine("  HARBOR_THEME     dark | light | system (default: dark).");
    Console.WriteLine("  HARBOR_MODEL     provider/model (default: kilocode/tencent/hy3:free)");
    Console.WriteLine("  HARBOR_STORAGE   memory | jsonl (default: memory)");
    Console.WriteLine("  HARBOR_LOGLEVEL  Trace|Debug|Information|Warning|Error (default: Information)");
    return 0;
}

App.ShellMode = ResolveShellMode(args);
App.ThemeMode = ResolveThemeMode(args);
App.ShowGallery = HasGalleryFlag(args);

using var host = AppHost.BuildAsync(args).GetAwaiter().GetResult();

AppBuilder.Configure<App>()
    .UsePlatformDetect()
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
    .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

return 0;

// Local functions

// Resolve the shell mode from CLI args + env var. Returns "classic" or "orca".
// Unknown values fall back to "classic" (with a stderr warning) so a typo
// never breaks the app launch.
static string ResolveShellMode(string[] args)
{
    // --shell <mode> takes precedence.
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--shell", StringComparison.OrdinalIgnoreCase))
        {
            string value = args[i + 1].Trim().ToLowerInvariant();
            return value switch
            {
                "orca" => "orca",
                "classic" => "classic",
                _ => LogFallback($"unknown --shell value '{args[i + 1]}', falling back to classic")
            };
        }
    }

    // HARBOR_SHELL env var.
    string? env = Environment.GetEnvironmentVariable("HARBOR_SHELL");
    if (!string.IsNullOrWhiteSpace(env))
    {
        string value = env.Trim().ToLowerInvariant();
        return value switch
        {
            "orca" => "orca",
            "classic" => "classic",
            _ => LogFallback($"unknown HARBOR_SHELL value '{env}', falling back to classic")
        };
    }

    return "classic";

    static string LogFallback(string message)
    {
        Console.Error.WriteLine($"[HARBOR_SHELL] {message}");
        return "classic";
    }
}

// Resolve the theme mode from CLI args + env var. Returns "dark", "light", or "system".
// Unknown values fall back to "dark" (with a stderr warning).
static string ResolveThemeMode(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase))
        {
            string value = args[i + 1].Trim().ToLowerInvariant();
            return value switch
            {
                "dark" => "dark",
                "light" => "light",
                "system" => "system",
                _ => LogThemeFallback($"unknown --theme value '{args[i + 1]}', falling back to dark")
            };
        }
    }

    string? themeEnv = Environment.GetEnvironmentVariable("HARBOR_THEME");
    if (!string.IsNullOrWhiteSpace(themeEnv))
    {
        string value = themeEnv.Trim().ToLowerInvariant();
        return value switch
        {
            "dark" => "dark",
            "light" => "light",
            "system" => "system",
            _ => LogThemeFallback($"unknown HARBOR_THEME value '{themeEnv}', falling back to dark")
        };
    }

    return "dark";

    static string LogThemeFallback(string message)
    {
        Console.Error.WriteLine($"[HARBOR_THEME] {message}");
        return "dark";
    }
}

static bool HasGalleryFlag(string[] args)
{
    return args.Contains("--gallery", StringComparer.OrdinalIgnoreCase);
}
