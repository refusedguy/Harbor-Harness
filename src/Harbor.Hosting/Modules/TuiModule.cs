using System.Collections.Frozen;
using Harbor.Hosting.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Filesystem;
using Harbor.Abstractions.Models;
using Harbor.Application.Filesystem;
using Harbor.Ui.Framework.Services;
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

        // #581: `Build` merges the plugin-contributed backends (#581's new
        // `IPluginLoadHost.RegisterTuiBackend` door) into the compiled-in set, so a
        // plugin backend and a compiled-in backend are one namespace with one
        // fallback rule — and the swap table below picks up both.
        FrozenDictionary<string, ITuiRendererFactory> registry =
            TuiBackendRegistry.Build(ctx.Registries.TuiBackends);
        ITuiRendererFactory backend = TuiBackendRegistry.Resolve(registry, requested);
        string tui = backend.BackendId; // canonical id, aliases normalized for logging
        if (!registry.ContainsKey(requested.Trim()))
        {
            // #581: the "available" set is `registry.Keys` — the same source the
            // swap table and the resolution above read. There is no second list.
            ctx.Logger.LogWarning(
                "Unknown HARBOR_TUI '{Requested}'; falling back to '{Fallback}' (the single fallback rule). Available: {Available}",
                requested,
                tui,
                string.Join(", ", registry.Keys.Order(StringComparer.Ordinal)));
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

        // #667: the file-tree seam. `SystemDirectoryLister` is the only place in
        // the harness that walks a directory for a UI view, and it lives in
        // Application; `FileTreeLoader` owns the CancellationTokenSource and
        // publishes into the store above. Registered HERE, in the TUI module,
        // rather than in an app, because the panel that needs it is a TUI panel:
        // `PanelServices.FromContainer` picks it up and every host that composes
        // a renderer gets it, with no per-app wiring to forget. A host that
        // resolves neither simply gets a null loader and a panel that says so.
        services.AddSingleton<IDirectoryLister, SystemDirectoryLister>();
        services.AddSingleton<IFileTreeLoader, FileTreeLoader>();

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

            // #584: the swap table is derived from the same registry the startup
            // backend was picked from — the `registry` built once above, captured here.
            // This used to be a second hand-written list of `pipeline.Register(id, …)`
            // calls that had fallen six backends behind, so `/renderer` reported three
            // available backends while six more were running in the same process. It
            // was also a second place to keep a backend's CONSTRUCTION details in sync:
            // #470's `panelServices:` on the cellforge entry had to be duplicated there
            // or the swap target silently lost its panel dependencies.
            //
            // The `#if` guards live on the array in TuiBackendRegistry, so they are
            // inherited here for free; `Register` replaces by id, so the loop is
            // idempotent, and every factory takes `sp` — the same provider this
            // lambda already resolves the shared `UiStore` from.
            foreach (KeyValuePair<string, ITuiRendererFactory> entry in registry)
            {
                ITuiRendererFactory factory = entry.Value;
                pipeline.Register(entry.Key, () => factory.Create(sp));
            }

            return pipeline;
        });

        services.AddSingleton<RuntimeRendererSwapMiddleware>(sp => new RuntimeRendererSwapMiddleware(
            sp.GetRequiredService<IRendererPipeline>(),
            ctx.Options.RuntimeSwappable,
            sp.GetRequiredService<ILogger<RuntimeRendererSwapMiddleware>>()));

        return services;
    }
}
