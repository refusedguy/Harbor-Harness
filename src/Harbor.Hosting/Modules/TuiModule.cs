using Harbor.Abstractions.Tui;
using Harbor.Tui.Plain;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class TuiModule
{
    /// <summary>
    ///     Renderer switch by name. Without the Spectre feature flag only the
    ///     plain renderer exists; HARBOR_TUI is ignored (as before).
    /// </summary>
    internal static IServiceCollection AddHarborTui(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        string defaultTui = string.IsNullOrEmpty(ctx.Options.DefaultTuiRenderer) || ctx.Options.DefaultTuiRenderer == "auto"
            ? "spectre-tui"
            : ctx.Options.DefaultTuiRenderer;
#if HARBOR_WITH_SPECTRE_TUI
        string tui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? defaultTui;
        ctx.Logger.LogInformation("TUI renderer: {Tui}", tui);
#else
        const string tui = "plain";
        ctx.Logger.LogInformation("TUI renderer: {Tui} (HARBOR_WITH_SPECTRE_TUI forces plain)", tui);
#endif
        services.AddSingleton<ITuiRenderer>(sp =>
        {
#if HARBOR_WITH_SPECTRE_TUI
            return tui.ToLowerInvariant() switch
            {
                "plain" => new PlainTuiRenderer(),
                "spectre" => new Harbor.Tui.Spectre.SpectreTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Spectre.SpectreTuiRenderer>>()),
                "fullscreen" => new Harbor.Tui.Spectre.Fullscreen.FullscreenTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Spectre.Fullscreen.FullscreenTuiRenderer>>()),
                "spectre-tui" => new Harbor.Tui.SpectreTui.SpectreTuiRenderer(
                    sp.GetRequiredService<ILogger<Harbor.Tui.SpectreTui.SpectreTuiRenderer>>(),
                    sp.GetService<PanelRegistry>()),
                "terminal-gui" => new Harbor.Tui.TerminalGui.TerminalGuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.TerminalGui.TerminalGuiRenderer>>()),
                "termina" => new Harbor.Tui.Termina.TerminaRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Termina.TerminaRenderer>>()),
                "razor" => new Harbor.Tui.RazorConsole.RazorConsoleRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.RazorConsole.RazorConsoleRenderer>>()),
                _ => new Harbor.Tui.Ansi.AnsiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Ansi.AnsiTuiRenderer>>())
            };
#else
            return new PlainTuiRenderer();
#endif
        });
        return services;
    }
}
