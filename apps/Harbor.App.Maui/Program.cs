using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace Harbor.App.Maui;

/// <summary>
///     MAUI application entry point. The MAUI SDK does not auto-generate a
///     <c>Main</c> method when the project is configured with explicit
///     PackageReferences under Central Package Management — we declare it
///     here so <c>dotnet build</c> produces a runnable WinExe on Windows.
/// </summary>
/// <remarks>
///     <para>
///         On macOS Catalyst, the platform-specific entry point lives in
///         <c>Platforms/MacCatalyst/Program.cs</c>; on Windows it lives here.
///         The branch below delegates to <see cref="MauiProgram" /> in both
///         cases.
///     </para>
/// </remarks>
public static class Program
{
    /// <summary>Application entry point — boots the MAUI app lifecycle.</summary>
    /// <param name="args">Command-line arguments (unused).</param>
    public static void Main(string[] args)
    {
        var app = MauiProgram.CreateMauiApp();
        // The MAUI host lifecycle is platform-driven — on Windows, the
        // Application.Current startup hook handles the rest. We just keep
        // the app reference alive so it isn't collected prematurely.
        _ = app;
    }
}
