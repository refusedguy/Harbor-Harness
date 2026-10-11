using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Ui.Framework.Panels;
/// <summary>
///     Provider contract for one dockable panel. Implementations live either in the
///     CellForge host assembly (the builtins, under <c>Harbor.Tui.CellForge</c>) or in
///     plugin assemblies. The host queries <see cref="Build" /> every frame the panel is
///     visible; <see cref="OnKey" /> is invoked only while the panel owns focus
///     (<see cref="TuiPanelState.Focused" />).
/// </summary>
/// <remarks>
///     <para>
///         <b>Widget type — return text rows, not a widget.</b> <see cref="Build" />
///         returns <see cref="object" /> because this project is intentionally free of
///         any TUI-framework dependency, but the signature is <b>not</b> an invitation
///         to return a framework widget. The only renderer with a panel path is
///         CellForge, and its decoder
///         (<c>CellForgePanelAdapter.WidgetToRows</c>) matches exactly three shapes —
///         <see cref="string" />, an <c>IReadOnlyList&lt;string&gt;</c> and an
///         <c>IEnumerable&lt;string&gt;</c> — and flattens each into rows. <b>Anything
///         else falls through to <c>widget.ToString()</c>, so a returned widget object
///         paints its own type name.</b> There is no cast to a native widget type in any
///         shipped renderer; <c>AnsiPlain</c> and <c>NickConsoleEx</c> have no panel
///         path at all. All 12 in-tree providers return
///         <c>PanelText.Clip(rows, ctx.Width, ctx.Height)</c>. The <c>object</c> return
///         leaves room for a native widget; the decoder has no case for one yet, which
///         is #564's open question and an owner decision under the #555 feature freeze,
///         not something a provider can rely on.
///     </para>
///     <para>
///         <b>Purity:</b> <see cref="Build" /> MUST be side-effect free (read
///         <see cref="PanelContext.State" />, return rows). <see cref="OnKey" /> may
///         mutate provider-local cache but must dispatch state transitions through
///         <c>UiStore.Dispatch</c> via the supplied services — never mutate the
///         <see cref="UiState" /> record directly.
///     </para>
///     <para>
///         <b>Generic layout:</b> the side-effect-free discipline above is the
///         panel-side instance of the composition contract documented in
///         <c>docs/GENERIC_LAYOUT.md</c> (§1 node contract, §4 Build purity
///         rule). A provider is a leaf in that sense: <see cref="Build" />
///         is its <c>Measure</c> (answer, never mutate); the host owns
///         arrange/paint. New composable nodes land in
///         <c>Harbor.Tui.CellForge.Engine/Rendering</c>, BCL-only, with zero
///         edits to the chat layer — see the doc's §3 forbids.
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
    public string Id { get; }

    /// <summary>Human-readable title shown in the panel's tab/border.</summary>
    public string Title { get; }

    /// <summary>Where the panel docks by default when first shown.</summary>
    public TuiPanelPlacement DefaultPlacement { get; }

    /// <summary>
    ///     Default size: rows for <see cref="TuiPanelPlacement.Top" /> /
    ///     <see cref="TuiPanelPlacement.Bottom" />, columns for
    ///     <see cref="TuiPanelPlacement.Left" /> / <see cref="TuiPanelPlacement.Right" />.
    /// </summary>
    public int DefaultSize { get; }

    /// <summary>
    ///     Build the panel's text rows for the current frame. Called only when the
    ///     panel is in <see cref="TuiPanelState.Visible" />, <see cref="TuiPanelState.Focused" />,
    ///     or <see cref="TuiPanelState.Pinned" /> — never when <see cref="TuiPanelState.Hidden" />.
    /// </summary>
    /// <param name="ctx">Per-frame context (state + geometry + typed <see cref="PanelServices" />).</param>
    /// <returns>
    ///     Rows of text: a <see cref="string" />, or an
    ///     <c>IReadOnlyList&lt;string&gt;</c> / <c>IEnumerable&lt;string&gt;</c>. The
    ///     builtins clip to the dock with
    ///     <c>PanelText.Clip(rows, ctx.Width, ctx.Height)</c> — a provider is
    ///     responsible for its own geometry. Any other type is flattened by
    ///     <c>ToString()</c> and paints its own type name. Return
    ///     <see langword="null" /> to render an empty placeholder.
    /// </returns>
    public object? Build(PanelContext ctx);

    /// <summary>
    ///     Handle a key press while this panel owns focus. Use
    ///     <c>UiStore.Dispatch</c> (via <c>ctx.Deps.Store</c>) to drive transitions —
    ///     do NOT mutate <see cref="UiState" /> in place. #470: the context
    ///     carries a typed <see cref="PanelServices" /> bag, not a container, and
    ///     every field of it is optional — a panel must degrade, never throw.
    /// </summary>
    /// <param name="key">The pressed key (already translated to <see cref="UiKey" />).</param>
    /// <param name="ctx">Per-frame context.</param>
    /// <returns>
    ///     <see langword="true" /> if the key was consumed (host skips default handling);
    ///     <see langword="false" /> to fall through to the host's default key map.
    /// </returns>
    public bool OnKey(UiKey key, PanelContext ctx);
}
