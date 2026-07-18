using Harbor.Tui.Abstractions.State;
namespace Harbor.Tui.Abstractions.Panels;

/// <summary>
///     Provider contract for one dockable panel. Implementations live either in the
///     SpectreTUI host assembly (builtins) or in plugin assemblies. The host queries
///     <see cref="Build" /> every frame the panel is visible; <see cref="OnKey" /> is
///     invoked only while the panel owns focus (<see cref="TuiPanelState.Focused" />).
/// </summary>
/// <remarks>
///     <para>
///         <b>Widget type:</b> <see cref="Build" /> returns <see cref="object" /> because
///         <c>Harbor.Tui.Abstractions</c> is intentionally free of any TUI-framework
///         dependency. Each concrete renderer casts the returned widget to its native
///         widget type — SpectreTUI panels return <c>Spectre.Tui.IWidget</c>. Plugin
///         authors that want to support multiple renderers should ship one
///         <see cref="IPanelProvider" /> per renderer assembly.
///     </para>
///     <para>
///         <b>Purity:</b> <see cref="Build" /> MUST be side-effect free (read
///         <see cref="PanelContext.State" />, return a widget). <see cref="OnKey" /> may
///         mutate provider-local cache but must dispatch state transitions through
///         <c>UiStore.Dispatch</c> via the supplied services — never mutate the
///         <see cref="UiState" /> record directly.
///     </para>
///     <para>
///         <b>Thread safety:</b> the host may call <see cref="Build" /> from the render
///         thread and <see cref="OnKey" /> from the input thread concurrently.
///         Implementations MUST be thread-safe (prefer immutable state, no shared
///         mutable fields without synchronization).
///     </para>
/// </remarks>
public interface IPanelProvider
{
    /// <summary>Stable, lowercase panel id (e.g. <c>"todo-list"</c>).</summary>
    string Id { get; }

    /// <summary>Human-readable title shown in the panel's tab/border.</summary>
    string Title { get; }

    /// <summary>Where the panel docks by default when first shown.</summary>
    TuiPanelPlacement DefaultPlacement { get; }

    /// <summary>
    ///     Default size: rows for <see cref="TuiPanelPlacement.Top" /> /
    ///     <see cref="TuiPanelPlacement.Bottom" />, columns for
    ///     <see cref="TuiPanelPlacement.Left" /> / <see cref="TuiPanelPlacement.Right" />.
    /// </summary>
    int DefaultSize { get; }

    /// <summary>
    ///     Build a renderer-native widget for the current frame. Called only when the
    ///     panel is in <see cref="TuiPanelState.Visible" />, <see cref="TuiPanelState.Focused" />,
    ///     or <see cref="TuiPanelState.Pinned" /> — never when <see cref="TuiPanelState.Hidden" />.
    /// </summary>
    /// <param name="ctx">Per-frame context (state + geometry + services).</param>
    /// <returns>
    ///     A renderer-native widget (e.g. <c>Spectre.Tui.IWidget</c> for SpectreTUI).
    ///     Return <see langword="null" /> to render an empty placeholder.
    /// </returns>
    object? Build(PanelContext ctx);

    /// <summary>
    ///     Handle a key press while this panel owns focus. Use
    ///     <c>UiStore.Dispatch</c> (via <c>ctx.Services</c>) to drive transitions —
    ///     do NOT mutate <see cref="UiState" /> in place.
    /// </summary>
    /// <param name="key">The pressed key (already translated to <see cref="UiKey" />).</param>
    /// <param name="ctx">Per-frame context.</param>
    /// <returns>
    ///     <see langword="true" /> if the key was consumed (host skips default handling);
    ///     <see langword="false" /> to fall through to the host's default key map.
    /// </returns>
    bool OnKey(UiKey key, PanelContext ctx);
}
