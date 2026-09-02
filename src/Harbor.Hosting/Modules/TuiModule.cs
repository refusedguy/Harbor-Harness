using Harbor.Abstractions.Tui;
using Harbor.Hosting.Rendering;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
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
        // CellForge lives in src/Harbor.Tui.CellForge (always compiled in) and is not gated by
        // HARBOR_WITH_SPECTRE_TUI — honor it even when the flag is off. Both the
        // canonical `cellforge` id and the legacy `consoleex` alias select it.
        string envTui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(envTui) && envTui.Trim() is "cellforge" or "consoleex")
        {
            string consoleTui = "cellforge"; // canonical id, alias normalized for logging
            ctx.Logger.LogInformation("TUI renderer: {Tui} (CellForge always enabled)", consoleTui);
            defaultTui = consoleTui; // fall through to ITuiRenderer switch below
        }
#if HARBOR_WITH_SPECTRE_TUI
        string tui = string.IsNullOrEmpty(envTui) ? defaultTui : envTui.Trim();
        // env=consoleex already handled above; keep resolved value for logging consistency.
        if (string.IsNullOrWhiteSpace(tui))
            tui = defaultTui;
        ctx.Logger.LogInformation("TUI renderer: {Tui}", tui);
#elif HARBOR_WITH_NICK_CONSOLE_EX
        string tui;
        if (!string.IsNullOrWhiteSpace(envTui) && envTui.Trim() is "cellforge" or "consoleex")
        {
            tui = "cellforge";
        }
        else if (!string.IsNullOrWhiteSpace(envTui) && envTui.Trim() == "nickconsoleex")
        {
            tui = "nickconsoleex";
        }
        else if (!string.IsNullOrWhiteSpace(defaultTui) && defaultTui is "cellforge" or "consoleex")
        {
            tui = "cellforge";
        }
        else if (defaultTui == "nickconsoleex")
        {
            tui = "nickconsoleex";
        }
        else
        {
            tui = "plain";
        }

        ctx.Logger.LogInformation("TUI renderer: {Tui} (Spectre off, CellForge always enabled)", tui);
#else
        string tui = !string.IsNullOrWhiteSpace(envTui) && envTui.Trim() is "cellforge" or "consoleex"
            ? "cellforge"
            : !string.IsNullOrWhiteSpace(defaultTui) && defaultTui is "cellforge" or "consoleex"
                ? "cellforge"
                : "plain";
        ctx.Logger.LogInformation("TUI renderer: {Tui} (CellForge always enabled, Spectre off)", tui);
#endif
        services.AddSingleton<ITuiRenderer>(sp =>
        {
            // CellForge is always available — handle before Spectre fallback.
            if (tui is "cellforge" or "consoleex")
            {
                // Phase 2 adapter: CellForgeTuiRenderer serves the event-driven
                // path through the AnsiWriter SGR automaton. The interactive
                // raw-mode entry remains ReplRunner (ScreenSession), which
                // bypasses ITuiRenderer entirely.
                return new Harbor.Tui.CellForge.CellForgeTuiRenderer(
                    sp.GetRequiredService<ILogger<Harbor.Tui.CellForge.CellForgeTuiRenderer>>());
            }
#if HARBOR_WITH_NICK_CONSOLE_EX
            // Phase 3: ADDITIVE backend — nickprotop/ConsoleEx window system.
            if (tui == "nickconsoleex")
            {
                return new Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer(
                    sp.GetRequiredService<ILogger<Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer>>());
            }
#endif

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
                _ => new Harbor.Tui.AnsiPlain.AnsiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.AnsiPlain.AnsiTuiRenderer>>())
            };
#else
            return tui.ToLowerInvariant() switch
            {
                "plain" => new PlainTuiRenderer(),
                "consoleex" or "cellforge" => new Harbor.Tui.CellForge.CellForgeTuiRenderer(
                    sp.GetRequiredService<ILogger<Harbor.Tui.CellForge.CellForgeTuiRenderer>>()),
                _ => new Harbor.Tui.AnsiPlain.AnsiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.AnsiPlain.AnsiTuiRenderer>>())
            };
#endif
        });

        // Phase 6.3: hot-swappable renderer runtime. The pipeline owns the
        // published renderer (CAS-gated swaps), restores the UiState snapshot
        // into the new backend, and disposes the old one exactly once. The
        // ITuiRenderer registration above stays the startup backend; the
        // pipeline publishes it as the initial slot.
        services.AddSingleton<IRendererPipeline>(sp =>
        {
            var pipeline = new RendererPipeline(
                sp.GetRequiredService<ITuiRenderer>(),
                tui.ToLowerInvariant(),
                sp.GetService<UiStore>(),
                sp.GetRequiredService<ILogger<RendererPipeline>>());

            pipeline.Register("cellforge", () => new Harbor.Tui.CellForge.CellForgeTuiRenderer(
                sp.GetRequiredService<ILogger<Harbor.Tui.CellForge.CellForgeTuiRenderer>>()));
            pipeline.Register("ansi", () => new Harbor.Tui.AnsiPlain.AnsiTuiRenderer(
                sp.GetRequiredService<ILogger<Harbor.Tui.AnsiPlain.AnsiTuiRenderer>>()));
            pipeline.Register("plain", () => new Harbor.Tui.AnsiPlain.PlainTuiRenderer());
#if HARBOR_WITH_NICK_CONSOLE_EX
            pipeline.Register("nickconsoleex", () => new Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer(
                sp.GetRequiredService<ILogger<Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer>>()));
#endif
            return pipeline;
        });

        services.AddSingleton<RuntimeRendererSwapMiddleware>(sp => new RuntimeRendererSwapMiddleware(
            sp.GetRequiredService<IRendererPipeline>(),
            ctx.Options.RuntimeSwappable,
            sp.GetRequiredService<ILogger<RuntimeRendererSwapMiddleware>>()));

        return services;
    }
}
