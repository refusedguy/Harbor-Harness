using System.Collections.Frozen;
using Harbor.Hosting.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class TuiModule
{
    /// <summary>
    ///     Renderer selection by name (issue #175: strategy registry —
    ///     <see cref="TuiBackendRegistry"/> — instead of per-#if string
    ///     switches). Without the Spectre feature flag the registry holds
    ///     only the baseline backends and unknown ids fall back to plain;
    ///     with the flag they fall back to ansi. Either way there is exactly
    ///     one fallback rule, and it is logged — never silent.
    /// </summary>
    internal static IServiceCollection AddHarborTui(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        string defaultTui = string.IsNullOrEmpty(ctx.Options.DefaultTuiRenderer) || ctx.Options.DefaultTuiRenderer == "auto"
            ? "spectre-tui"
            : ctx.Options.DefaultTuiRenderer;
        // CellForge lives in src/Harbor.Tui.CellForge (always compiled in) and is not gated by
        // HARBOR_WITH_SPECTRE_TUI — the registry resolves its canonical
        // `cellforge` id and the legacy `consoleex` alias on every build.
        string envTui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? string.Empty;
        string requested = string.IsNullOrWhiteSpace(envTui) ? defaultTui : envTui.Trim();

        FrozenDictionary<string, ITuiRendererFactory> registry = TuiBackendRegistry.Build();
        ITuiRendererFactory backend = TuiBackendRegistry.Resolve(registry, requested);
        string tui = backend.BackendId; // canonical id, aliases normalized for logging
        if (!registry.ContainsKey(requested.Trim()))
        {
            ctx.Logger.LogWarning(
                "Unknown HARBOR_TUI '{Requested}'; falling back to '{Fallback}' (the single fallback rule)",
                requested,
                tui);
        }
#if HARBOR_WITH_SPECTRE_TUI
        ctx.Logger.LogInformation("TUI renderer: {Tui}", tui);
#elif HARBOR_WITH_NICK_CONSOLE_EX
        ctx.Logger.LogInformation("TUI renderer: {Tui} (Spectre off, CellForge always enabled)", tui);
#else
        ctx.Logger.LogInformation("TUI renderer: {Tui} (CellForge always enabled, Spectre off)", tui);
#endif
        services.AddSingleton<ITuiRenderer>(sp => backend.Create(sp));

        // Issue #77: the pipeline reads its snapshot-restore state from the
        // DI-shared UiStore. It must be registered here (CLI composition
        // root) — previously only Avalonia registered it, so the CLI host
        // always passed null and restore-across-swap was silently dead.
        // Renderers keep their own private stores; the pipeline reads this
        // shared snapshot, never writes it.
        services.AddSingleton<UiStore>();

        // Phase 6.3: hot-swappable renderer runtime. The pipeline owns the
        // published renderer (CAS-gated swaps), restores the UiState snapshot
        // into the new backend, and disposes the old one exactly once. The
        // ITuiRenderer registration above stays the startup backend; the
        // pipeline publishes it as the initial slot.
        services.AddSingleton<IRendererPipeline>(sp =>
        {
            var pipeline = new RendererPipeline(
                sp.GetRequiredService<ITuiRenderer>(),
                tui,
                sp.GetRequiredService<UiStore>(),
                sp.GetRequiredService<ILogger<RendererPipeline>>());

            pipeline.Register("cellforge", () => new Harbor.Tui.CellForge.CellForgeTuiRenderer(
                sp.GetRequiredService<ILogger<Harbor.Tui.CellForge.CellForgeTuiRenderer>>(),
                store: sp.GetRequiredService<UiStore>()));
            pipeline.Register("ansi", () => new Harbor.Tui.AnsiPlain.AnsiTuiRenderer(
                sp.GetRequiredService<ILogger<Harbor.Tui.AnsiPlain.AnsiTuiRenderer>>(),
                store: sp.GetRequiredService<UiStore>()));
            pipeline.Register("plain", () => new Harbor.Tui.AnsiPlain.PlainTuiRenderer(
                store: sp.GetRequiredService<UiStore>()));
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
