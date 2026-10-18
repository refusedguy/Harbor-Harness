using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Issue #381: the Cmd/Ctrl-J worktree jump palette as a <b>centred modal
///     overlay</b> instead of a Right-docked panel. Reuses the existing
///     overlay primitives — no new z-layer system: the host pushes this layer
///     onto <c>LayoutTree.Overlays</c> (the same
///     <see cref="OverlayStack" /> that seats the dialog / toast / diff viewer)
///     and calls <see cref="Sync" /> per frame. The IOverlayLayer skeleton —
///     the viewport field, the single-viewport <c>Sync(Rect)</c> overload,
///     the <see cref="Paint" /> guard chain
///     and the <see cref="Opaque" /> / <see cref="HitTransparent" /> /
///     <see cref="IsModal" /> constants — lives in
///     <see cref="OverlayLayerBase{TOverlay}"/> (#1537); this seat keeps only
///     its deltas: its provider-backed <see cref="Bounds"/>, the
///     provider-context <see cref="Sync(Rect, PanelContext)"/> and its
///     <see cref="OnKey"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Geometry.</b> <see cref="Bounds" /> is
///         <see cref="OverlayPopup.CenteredRect" /> of the viewport: the box is
///         <see cref="CellForgeJumpPalettePanel.DefaultSize" /> columns wide and
///         as tall as the rows the provider built (clamped to
///         <see cref="MinHeight" />…<see cref="MaxHeight" />, and to the viewport
///         itself), so tiny terminals degrade to a full-screen box instead of
///         throwing.
///     </para>
///     <para>
///         <b>Input barrier.</b> <see cref="IsModal" /> is
///         <see langword="true" />, so any host that routes through
///         <see cref="OverlayStack.RouteKey" /> would stop at this layer and let
///         panels and the composer beneath starve — the "no typing leak" half of
///         #381. The provider consumes printable keys itself (see
///         <see cref="CellForgeJumpPalettePanel.OnKey" />).
///     </para>
///     <para>
///         <b>Nothing in the product routes that way yet.</b> (#858)
///         <see cref="OverlayStack.RouteKey" /> has no product call site, and no
///         product code constructs this layer at all — <c>new</c>
///         <see cref="CellForgeJumpPaletteOverlayLayer" /> appears only under
///         <c>tests/</c>. The /jump frame the user actually sees is the command
///         palette frame, keyed by the hand-written cascade in
///         <c>ReplInputLoop.HandleKeyAsync</c> (#857). What this class documents
///         is the barrier a host would get IF it routed through the stack, not a
///         path the product takes today.
///     </para>
///     <para>
///         <b>Seeding.</b> Painting delegates to
///         <see cref="CellForgePanelAdapter.RenderToRows(IPanelProvider, PanelContext)" />,
///         which is the same build path the dock used — so the provider still
///         seeds once on the first painted frame and never spawns <c>git</c> on
///         an unchanged frame.
///     </para>
/// </remarks>
public sealed class CellForgeJumpPaletteOverlayLayer : OverlayLayerBase<CellForgeJumpPalettePanel>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "jump";

    /// <summary>Widest the centred box may grow (keeps long paths readable on wide terminals).</summary>
    internal const int MaxWidth = 96;

    /// <summary>Tallest the centred box may grow — leaves the chat band readable beneath.</summary>
    internal const int MaxHeight = 24;

    /// <summary>Smallest useful box (header + separators + hint + one row).</summary>
    internal const int MinHeight = 5;

    private PanelContext _ctx = new(new UiState(), 0, 0);
    private IReadOnlyList<string> _rows = Array.Empty<string>();

    /// <summary>
    ///     Wrap a palette provider (the host owns the instance and its model).
    ///     The three flags are the seat's PRIM2c constants: the box blanks and
    ///     occludes what it covers, captures hit-tests inside its bounds, and
    ///     owns the keyboard while it is up.
    /// </summary>
    public CellForgeJumpPaletteOverlayLayer(CellForgeJumpPalettePanel panel)
        : base(panel, LayerId, opaque: true, hitTransparent: false, isModal: true)
    {
    }

    /// <summary>
    ///     The centred box: as tall as the rows the provider built (clamped to
    ///     <see cref="MinHeight"/>…<see cref="MaxHeight"/> and to the viewport)
    ///     and as wide as the provider's build width. The clamp to the viewport
    ///     is <see cref="OverlayPopup.CenteredRect" />'s job, so a five-row
    ///     palette on a four-row terminal degrades to the full viewport instead
    ///     of painting outside it.
    /// </summary>
    protected override Rect ComputeBoundsCore(Rect viewport)
    {
        int height = Math.Clamp(_rows.Count, MinHeight, MaxHeight);
        return OverlayPopup.CenteredRect(viewport, BoxWidth(), height);
    }

    /// <summary>
    ///     Box width in columns — independent of the row count, so
    ///     <see cref="Sync" /> can build the provider at its final width before
    ///     the height is known. <see cref="OverlayPopup.CenteredRect" /> clamps
    ///     the final box to the viewport in both dimensions. The terminal cap
    ///     keeps long paths readable on wide terminals.
    /// </summary>
    private int BoxWidth() => Math.Min(MaxWidth, Overlay.DefaultSize);

    /// <summary>
    ///     The state half of the base's <see cref="Visible" /> contract: the
    ///     palette has content to draw exactly when its panel state is anything
    ///     but <see cref="TuiPanelState.Hidden" /> in the snapshot.
    /// </summary>
    /// <inheritdoc />
    protected override bool IsShownCore => IsOpenIn(_ctx.State);

    /// <summary>
    ///     The palette is open when its panel is anything but
    ///     <see cref="TuiPanelState.Hidden" /> in the snapshot — the same
    ///     reducer-driven signal the dock uses, so Ctrl+J and Esc keep toggling
    ///     it through <c>AppMsg.TogglePanel</c>. <see cref="Visible"/> adds the
    ///     positive-size geometry on top of this state half, so a closed
    ///     palette means: no box, no paint, and — critically — no modal
    ///     barrier, so the composer and panels get their keys back.
    /// </summary>
    private static bool IsOpenIn(UiState state) =>
        state.Ui.PanelStates.TryGetValue(OverlayIds.JumpPalette, out var panelState)
        && panelState != TuiPanelState.Hidden;

    /// <summary>
    ///     Refreshes the viewport the centred box is computed from and the
    ///     per-frame provider context, then snapshots the provider's rows (the
    ///     ENG12 replace-wholesale pattern the dock leaves use — one build per
    ///     frame, never one per <see cref="Bounds" /> read). Call once per frame
    ///     (or on resize); the single-viewport <c>Sync(Rect)</c> still updates
    ///     the viewport for hosts that have no context. A closed palette
    ///     empties the rows besides, so the next <see cref="Visible" /> read
    ///     is false even before the host's next context refresh arrives.
    /// </summary>
    public void Sync(Rect viewport, PanelContext context)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(context);
        base.Sync(viewport);
        _ctx = context;
        if (!IsOpenIn(context.State))
        {
            _rows = Array.Empty<string>();
            return;
        }

        // Build at the box width so the provider clips rows with its ellipsis
        // rule instead of a raw slice; the height comes from the row count.
        _rows = CellForgePanelAdapter.RenderToRows(
            Overlay,
            context with
            {
                Width = Math.Max(1, Math.Min(BoxWidth(), viewport.Width)),
                Height = Math.Max(1, viewport.Height),
            });
    }

    /// <summary>
    ///     Routes a decoded key to the palette. Returns false when the palette
    ///     is closed in the snapshot or when the key maps to no printable
    ///     chord it consumes (e.g. Ctrl/Alt composites) so the host keymap still
    ///     sees it.
    /// </summary>
    public bool OnKey(in KeyEvent key)
    {
        if (!Visible || !KeyEventMapper.TryMap(in key, out var dto))
        {
            return false;
        }

        var box = Bounds;
        return Overlay.OnKey(
            KeyEventAdapter.ToUiKey(dto),
            _ctx with { Width = Math.Max(1, box.Width), Height = Math.Max(1, box.Height) });
    }

    /// <summary>
    ///     Paints the snapshotted rows into the centred box (blanked first, so
    ///     the chat underneath never bleeds through). Rows past the box height
    ///     are dropped; the provider already clipped them to its width.
    /// </summary>
    protected override void PaintCore(ScreenBuffer buffer, Rect box)
    {
        OverlayPopup.Clear(buffer, box);

        // ENG12 #284 (TGui snapshot pattern): rows are replaced wholesale by
        // Sync — pin the reference for this draw pass.
        var rows = _rows;
        int n = Math.Min(rows.Count, box.Height);
        for (int i = 0; i < n; i++)
        {
            string row = rows[i] ?? string.Empty;
            buffer.SetText(box.X, box.Y + i, row.AsSpan(0, Math.Min(row.Length, box.Width)), CellStyle.Plain);
        }
    }
}
