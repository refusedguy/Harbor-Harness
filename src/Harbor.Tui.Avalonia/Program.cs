using Avalonia;
using Harbor.Tui.Avalonia;

// Standalone entry point — only used when running this project directly
// (`dotnet run --project src/Harbor.Tui.Avalonia`) for designer / XAML preview.
//
// The production path is via Harbor.Cli (HARBOR_TUI=avalonia), which calls
// AvaloniaTuiRenderer.RunInteractiveAsync and runs the Avalonia lifetime on a
// dedicated thread inside the renderer.

AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args);
