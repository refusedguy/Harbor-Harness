using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

// Cell-native builtin panels for the CellForge renderer (CF-E-002, TOP-1 #27).
// <remarks>
//     Same 7 panel contracts as the SpectreTUI builtins (identical
//     Id / Title / DefaultPlacement / DefaultSize so Alt+1..9 slots and
//     EnsureSeeded defaults line up), but rendered as plain cell rows
//     (IReadOnlyList<string>) instead of Spectre widgets. There is
//     intentionally no reference to contrib/tui/Harbor.Tui.SpectreTui here
//     (the architecture matrix forbids it) — CellForgePanelAdapter already
//     flattens string / IReadOnlyList<string> widgets without a ToString
//     round-trip.
//     Purity: every Build reads only ctx.State (+ DI services for help/logs,
//     the filesystem for file-tree) and returns freshly allocated rows — safe
//     to call from the render thread. OnKey never mutates UiState; state
//     transitions go through the explicit ctx.Store (#63). A null store /
//     service provider degrades gracefully: the key is still reported as
//     consumed, help/logs render fallback rows.
// </remarks>

/// <summary>
///     Shared base for the CellForge cell-native builtin panels (issue #192):
///     one panel = one file. Carries the four identity members so each panel
///     declares only <see cref="Build"/>; non-interactive panels inherit the
///     <c>false</c> <see cref="OnKey"/> default.
/// </summary>
public abstract class CellForgePanelBase : IPanelProvider
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Title { get; }

    /// <inheritdoc />
    public abstract TuiPanelPlacement DefaultPlacement { get; }

    /// <inheritdoc />
    public abstract int DefaultSize { get; }

    /// <inheritdoc />
    public abstract object? Build(PanelContext ctx);

    /// <inheritdoc />
    public virtual bool OnKey(UiKey key, PanelContext ctx) => false;
}
