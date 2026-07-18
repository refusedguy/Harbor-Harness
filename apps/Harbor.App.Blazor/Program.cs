using Harbor.App.Blazor.Configuration;
using Harbor.App.Blazor.Services;
using Harbor.App.Blazor.ViewModels;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Storage.Memory;
using Harbor.Ui.Framework.State;
using Excubo.Analyzers.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    // [Exposes(typeof(T))] declarations are validated by Excubo.Analyzers.DependencyInjectionValidation
    // (EDI01–EDI04). Program.Main builds the WebApplication but never returns the IServiceProvider
    // to a caller, so DI tests use BuildHostForTesting() below which mirrors the same registration
    // list without starting Kestrel.
    [Exposes(typeof(UiStore))]
    [Exposes(typeof(MemorySessionStore))]
    [Exposes(typeof(BlazorDispatcherAdapter))]
    [Exposes(typeof(ThemeService))]
    [Exposes(typeof(DialogService))]
    [Exposes(typeof(ToastService))]
    [Exposes(typeof(CommandPaletteService))]
    [Exposes(typeof(SessionBrowserService))]
    [Exposes(typeof(ProviderBrowserService))]
    [Exposes(typeof(IAppConfigStore<BlazorConfig>))]
    [Exposes(typeof(BlazorConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<BlazorConfig>))]
    [Exposes(typeof(ChatViewModel))]
    [Exposes(typeof(SessionListViewModel))]
    [Exposes(typeof(ProviderBrowserViewModel))]
    [Exposes(typeof(SettingsViewModel))]
    [Exposes(typeof(TokenUsageViewModel))]
    public static async Task Main(string[] args)
    {
        bool autoOpenArg = !Array.Exists(args, a => a is "--no-open-browser" or "--no-browser");

        WebApplication app = BuildApp(args);

        // Resolve BlazorConfig from the built host so Main can honour the
        // persisted ListenPort + AutoOpenBrowser preferences. The CLI flag
        // (--no-open-browser) still wins over the persisted preference.
        var blazorConfig = app.Services.GetRequiredService<BlazorConfig>();
        bool autoOpen = autoOpenArg && blazorConfig.AutoOpenBrowser;
        int port = blazorConfig.ListenPort <= 0 ? 5000 : blazorConfig.ListenPort;
        string url = $"http://localhost:{port}";

        // Wire the resolved URL into Kestrel's URL list so the printed banner
        // reflects the actual bound port. Without this, Kestrel falls back to
        // its default http://localhost:5000 and the banner lies — which broke
        // E2E tests that wait for "listening on" then hit the configured port.
        app.Urls.Clear();
        app.Urls.Add(url);

        // Print the listening banner ONLY after Kestrel has actually bound to
        // the port (IHostApplicationLifetime.ApplicationStarted fires after the
        // server is accepting connections). Printing earlier would race E2E
        // tests that see the banner then immediately HTTP-GET the port.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine($"Harbor Blazor listening on {url}");
            Console.WriteLine("Press Ctrl+C to stop.");
        });

        if (autoOpen)
        {
            lifetime.ApplicationStarted.Register(() => TryOpenBrowser(url));
        }

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Graceful Ctrl+C shutdown.
        }
    }

    /// <summary>
    ///     Builds the configured <see cref="WebApplication"/> without starting
    ///     Kestrel. Internal so <c>Harbor.App.Blazor.Tests</c> can resolve every
    ///     registered service from <see cref="WebApplication.Services"/> and
    ///     assert the DI container is complete. <see cref="Main"/> calls this
    ///     and then invokes <c>app.RunAsync()</c> on the result.
    /// </summary>
    /// <param name="args">CLI args forwarded to <see cref="WebApplication.CreateBuilder"/>.</param>
    /// <returns>A built (but not started) <see cref="WebApplication"/>.</returns>
    internal static WebApplication BuildApp(string[] args)
    {
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

        // ── Per-app Blazor configuration (~/.harbor/blazor.json) ──
        // Non-overlapping with CLI/Avalonia/WPF/MAUI config files AND with
        // the shared ~/.harbor/config.json.
        builder.Services.AddSingleton<IAppConfigStore<BlazorConfig>>(sp =>
            new JsonAppConfigStore<BlazorConfig>(
                new BlazorConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<BlazorConfig>>>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IAppConfigStore<BlazorConfig>>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            return result.IsSuccess ? result.Value : new BlazorConfig();
        });

        // ── Shared common configuration (~/.harbor/config.json) ──
        // CommonConfig holds API keys, default provider/model, storage backend,
        // log level, permissions, plugins, network, compaction — every field
        // that is shared across ALL Harbor apps. Loaded eagerly so the Blazor
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

        // ── Composite: CommonConfig + BlazorConfig ──
        builder.Services.AddSingleton<CompositeConfig<BlazorConfig>>(sp =>
            new CompositeConfig<BlazorConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<BlazorConfig>()));

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

        return app;
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
