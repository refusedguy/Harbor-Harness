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
        // ConsoleEx lives in src/Harbor.Tui.ConsoleEx (always compiled in) and is not gated by
        // HARBOR_WITH_SPECTRE_TUI — honor it even when the flag is off.
        string envTui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(envTui) && envTui.Trim().Equals("consoleex", StringComparison.OrdinalIgnoreCase))
        {
            string consoleTui = envTui.Trim();
            ctx.Logger.LogInformation("TUI renderer: {Tui} (ConsoleEx always enabled)", consoleTui);
            defaultTui = consoleTui; // fall through to ITuiRenderer switch below
        }
#if HARBOR_WITH_SPECTRE_TUI
        string tui = string.IsNullOrEmpty(envTui) ? defaultTui : envTui.Trim();
        // env=consoleex already handled above; keep resolved value for logging consistency.
        if (string.IsNullOrWhiteSpace(tui))
            tui = defaultTui;
        ctx.Logger.LogInformation("TUI renderer: {Tui}", tui);
#else
        string tui = !string.IsNullOrWhiteSpace(envTui) && envTui.Trim().Equals("consoleex", StringComparison.OrdinalIgnoreCase)
            ? envTui.Trim()
            : !string.IsNullOrWhiteSpace(defaultTui) && defaultTui.Equals("consoleex", StringComparison.OrdinalIgnoreCase)
                ? defaultTui
                : "plain";
        ctx.Logger.LogInformation("TUI renderer: {Tui} (ConsoleEx always enabled, Spectre off)", tui);
#endif
        services.AddSingleton<ITuiRenderer>(sp =>
        {
            // ConsoleEx is always available — handle before Spectre fallback.
            if (tui.Equals("consoleex", StringComparison.OrdinalIgnoreCase))
            {
                // Re-use the Ansi path's success type — the actual ConsoleEx
                // interactive entry is via ReplRunner (raw mode + ScreenSession),
                // but a non-interactive renderer still needs an ITuiRenderer.
                // Returning Ansi keeps one-shot commands working; interactive
                // ConsoleEx is entered through ReplRunner, not through ITuiRenderer.
                return new Harbor.Tui.Ansi.AnsiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Ansi.AnsiTuiRenderer>>());
            }

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
            return tui.ToLowerInvariant() switch
            {
                "plain" => new PlainTuiRenderer(),
                "consoleex" => new Harbor.Tui.Ansi.AnsiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Ansi.AnsiTuiRenderer>>()),
                _ => new Harbor.Tui.Ansi.AnsiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Ansi.AnsiTuiRenderer>>())
            };
#endif
        });
        return services;
    }
}
