using System.Collections.Frozen;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

// Issue #175 (OCP — string switch instead of Strategy): TUI backend
// construction moved out of TuiModule into one strategy per backend.
// Adding a backend = adding one factory class + one list entry below —
// no more N-way edits across #if branches in sync.

/// <summary>
///     Construction strategy for one TUI backend id.
/// </summary>
internal interface ITuiRendererFactory
{
    /// <summary>Canonical backend id (lowercase). Used for logging, pipeline ids and runtime swap targets.</summary>
    string BackendId { get; }

    /// <summary>Legacy/alternate spellings resolved to <see cref="BackendId"/> (e.g. <c>consoleex</c> → <c>cellforge</c>).</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>
    ///     Construct the renderer. Runs inside the <c>ITuiRenderer</c>
    ///     singleton factory lambda — resolving here is idiomatic MS DI
    ///     (#63), not service location.
    /// </summary>
    ITuiRenderer Create(IServiceProvider sp);
}

/// <summary>Plain pipes/CI renderer (<c>HARBOR_TUI=plain</c>).</summary>
internal sealed class PlainTuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "plain";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new PlainTuiRenderer(store: sp.GetRequiredService<UiStore>());
}

/// <summary>
///     ANSI-streaming renderer. The single fallback rule target when
///     Spectre backends are compiled in (see <see cref="TuiBackendRegistry"/>).
/// </summary>
internal sealed class AnsiTuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "ansi";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        // Issue #77: ansi writes must land in the DI-shared UiStore so
        // the pipeline restores them across renderer swaps.
        new Harbor.Tui.AnsiPlain.AnsiTuiRenderer(
            sp.GetRequiredService<ILogger<Harbor.Tui.AnsiPlain.AnsiTuiRenderer>>(),
            store: sp.GetRequiredService<UiStore>());
}

/// <summary>Canonical fullscreen cell-diff renderer (<c>cellforge</c>, legacy alias <c>consoleex</c>).</summary>
internal sealed class CellForgeTuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "cellforge";

    public IReadOnlyList<string> Aliases => ["consoleex"];

    public ITuiRenderer Create(IServiceProvider sp) =>
        // Phase 2 adapter: CellForgeTuiRenderer serves the event-driven
        // path through the AnsiWriter SGR automaton. The interactive
        // raw-mode entry remains ReplRunner (ScreenSession), which
        // bypasses ITuiRenderer entirely.
        // Issue #77: chat writes must land in the DI-shared UiStore so
        // the pipeline restores them across renderer swaps.
        new Harbor.Tui.CellForge.CellForgeTuiRenderer(
            sp.GetRequiredService<ILogger<Harbor.Tui.CellForge.CellForgeTuiRenderer>>(),
            store: sp.GetRequiredService<UiStore>(),
            // #470: one composition-time projection of the container into the
            // typed bag the panels read. The per-frame PanelContext carries this
            // value, never the IServiceProvider.
            panelServices: Harbor.Ui.Framework.Panels.PanelServices.FromContainer(sp));
}

#if HARBOR_WITH_NICK_CONSOLE_EX
/// <summary>Phase 3 additive backend over nickprotop/ConsoleEx (<c>HARBOR_TUI=nickconsoleex</c>).</summary>
internal sealed class NickConsoleExTuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "nickconsoleex";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer(
            sp.GetRequiredService<ILogger<Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer>>());
}
#endif

#if HARBOR_WITH_SPECTRE_TUI
/// <summary>Interactive Spectre shell (<c>HARBOR_TUI=spectre</c>).</summary>
internal sealed class SpectreTuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "spectre";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.Spectre.SpectreTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Spectre.SpectreTuiRenderer>>());
}

/// <summary>Fullscreen Spectre shell (<c>HARBOR_TUI=fullscreen</c>).</summary>
internal sealed class FullscreenTuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "fullscreen";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.Spectre.Fullscreen.FullscreenTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Spectre.Fullscreen.FullscreenTuiRenderer>>());
}

/// <summary>Default interactive shell (<c>HARBOR_TUI=spectre-tui</c>).</summary>
internal sealed class SpectreTuiShellRendererFactory : ITuiRendererFactory
{
    public string BackendId => "spectre-tui";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.SpectreTui.SpectreTuiRenderer(
            sp.GetRequiredService<ILogger<Harbor.Tui.SpectreTui.SpectreTuiRenderer>>(),
            sp.GetService<PanelRegistry>());
}

/// <summary>Terminal.Gui backend (<c>HARBOR_TUI=terminal-gui</c>).</summary>
internal sealed class TerminalGuiRendererFactory : ITuiRendererFactory
{
    public string BackendId => "terminal-gui";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.TerminalGui.TerminalGuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.TerminalGui.TerminalGuiRenderer>>());
}

/// <summary>Termina backend (<c>HARBOR_TUI=termina</c>).</summary>
internal sealed class TerminaRendererFactory : ITuiRendererFactory
{
    public string BackendId => "termina";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.Termina.TerminaRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Termina.TerminaRenderer>>());
}

/// <summary>RazorConsole backend (<c>HARBOR_TUI=razor</c>).</summary>
internal sealed class RazorConsoleRendererFactory : ITuiRendererFactory
{
    public string BackendId => "razor";

    public IReadOnlyList<string> Aliases => [];

    public ITuiRenderer Create(IServiceProvider sp) =>
        new Harbor.Tui.RazorConsole.RazorConsoleRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.RazorConsole.RazorConsoleRenderer>>());
}
#endif

/// <summary>
///     Lookup registry over the <see cref="ITuiRendererFactory"/>
///     strategies: id + alias normalization in one place, one fallback
///     rule, no silent default (unknown ids fall back loudly — TuiModule
///     logs a warning naming the requested id).
/// </summary>
internal static class TuiBackendRegistry
{
#if HARBOR_WITH_SPECTRE_TUI
    /// <summary>The single fallback rule when Spectre backends are compiled in: unknown ids render ANSI.</summary>
    internal const string FallbackBackendId = "ansi";
#else
    /// <summary>The single fallback rule without Spectre: unknown ids render plain.</summary>
    internal const string FallbackBackendId = "plain";
#endif

    /// <summary>Build the id/alias → factory index for the compiled-in backends.</summary>
    internal static FrozenDictionary<string, ITuiRendererFactory> Build()
    {
        ITuiRendererFactory[] factories =
        [
            new PlainTuiRendererFactory(),
            new AnsiTuiRendererFactory(),
            new CellForgeTuiRendererFactory(),
#if HARBOR_WITH_NICK_CONSOLE_EX
            new NickConsoleExTuiRendererFactory(),
#endif
#if HARBOR_WITH_SPECTRE_TUI
            new SpectreTuiRendererFactory(),
            new FullscreenTuiRendererFactory(),
            new SpectreTuiShellRendererFactory(),
            new TerminalGuiRendererFactory(),
            new TerminaRendererFactory(),
            new RazorConsoleRendererFactory(),
#endif
        ];
        var index = new Dictionary<string, ITuiRendererFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (ITuiRendererFactory factory in factories)
        {
            index[factory.BackendId] = factory;
            foreach (string alias in factory.Aliases)
            {
                index[alias] = factory;
            }
        }

        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolve a raw <c>HARBOR_TUI</c>/default value to its factory.
    ///     Normalization (trim + case-insensitive + aliases) is uniform
    ///     across builds; unknown ids hit <see cref="FallbackBackendId"/>.
    /// </summary>
    internal static ITuiRendererFactory Resolve(
        FrozenDictionary<string, ITuiRendererFactory> registry,
        string rawId)
    {
        if (registry.TryGetValue(rawId.Trim(), out ITuiRendererFactory? factory))
        {
            return factory;
        }

        return registry[FallbackBackendId];
    }
}
