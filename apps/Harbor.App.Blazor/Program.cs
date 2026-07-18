using Harbor.App.Blazor.Services;
using Harbor.App.Blazor.ViewModels;
using Harbor.Storage.Memory;
using Harbor.Tui.Abstractions.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harbor.App.Blazor;

/// <summary>
///     Entry point for the Harbor Blazor Server desktop app. Spins up Kestrel
///     on <c>http://localhost:5000</c>, opens the host browser, and serves the
///     Harbor chat UI over a SignalR circuit.
/// </summary>
/// <remarks>
///     <para>
///         <b>Run:</b> <c>dotnet run --project apps/Harbor.App.Blazor</c>
///     </para>
///     <para>
///         <b>Auto-open browser:</b> default on; disable with
///         <c>--no-open-browser</c>.
///     </para>
///     <para>
///         All UI state flows through <see cref="UiStore"/> (TEA/MVU pattern).
///         Razor components subscribe to <see cref="UiStore.Changed"/> and
///         re-render via <see cref="ComponentBase.InvokeAsync"/> marshalled by
///         <see cref="BlazorDispatcherAdapter"/>.
///     </para>
/// </remarks>
internal static class Program
{
    /// <summary>Launch the Blazor host.</summary>
    /// <param name="args">CLI args. Recognises <c>--no-open-browser</c>.</param>
    public static async Task Main(string[] args)
    {
        bool autoOpen = !Array.Exists(args, a => a is "--no-open-browser" or "--no-browser");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(o =>
            {
                o.MaximumReceiveMessageSize = 1_000_000; // 1 MiB for large code pastes
                o.ClientTimeoutInterval = TimeSpan.FromHours(2);
                o.HandshakeTimeout = TimeSpan.FromSeconds(30);
            });

        // Harbor services — minimal wiring so the UI compiles and runs even
        // without a configured provider. The user can configure providers via
        // the Settings page after first launch; the chat panel runs against
        // the local UiStore (TEA) and the session browser uses the in-memory
        // store as a fallback when no JSONL directory is configured.
        builder.Services.AddSingleton<UiStore>();
        builder.Services.AddSingleton<MemorySessionStore>();

        // App-local UI services.
        builder.Services.AddSingleton<BlazorDispatcherAdapter>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<DialogService>();
        builder.Services.AddSingleton<ToastService>();
        builder.Services.AddSingleton<CommandPaletteService>();
        builder.Services.AddSingleton<SessionBrowserService>();
        builder.Services.AddSingleton<ProviderBrowserService>();
        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<SessionListViewModel>();
        builder.Services.AddSingleton<ProviderBrowserViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<TokenUsageViewModel>();

        // Blazor circuit-scoped JS interop.
        builder.Services.AddScoped<HarborJsInterop>();

        WebApplication app = builder.Build();

        app.UseStaticFiles();
        app.UseAntiforgery();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        string url = "http://localhost:5000";
        if (autoOpen)
        {
            TryOpenBrowser(url);
        }

        Console.WriteLine($"Harbor Blazor listening on {url}");
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Graceful Ctrl+C shutdown.
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Best-effort — Linux servers without xdg-open will silently no-op.
            // The user can navigate manually to the URL printed to the console.
            Console.Error.WriteLine($"Could not auto-open browser: {ex.Message}. Open {url} manually.");
        }
    }
}
