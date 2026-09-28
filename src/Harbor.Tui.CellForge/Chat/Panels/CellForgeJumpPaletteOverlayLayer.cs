using Harbor.Tui.CellForge.Rendering;
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
///     and calls <see cref="Sync" /> per frame.
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
///         <see langword="true" />: while the palette is visible
///         <see cref="OverlayStack.HasModalBarrier" /> reports an active barrier
///         and keys route top-down through <see cref="OnKey" />, so panels and
///         the composer beneath never see the typed query (the "no typing leak"
///         half of #381). The provider consumes printable keys itself (see
///         <see cref="CellForgeJumpPalettePanel.OnKey" />).
///     </para>
///     <para>
///         <b>Seeding.</b> Painting delegates to
///         <see cref="CellForgePanelAdapter.RenderToRows(IPanelProvider, PanelContext)" />,
///         which is the same build path the dock used — so the provider still
///         seeds once on the first painted frame and never spawns <c>git</c> on
///         an unchanged frame.
///     </para>
/// </remarks>
public sealed class CellForgeJumpPaletteOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "jump";

    /// <summary>Widest the centred box may grow (keeps long paths readable on wide terminals).</summary>
    internal const int MaxWidth = 96;

    /// <summary>Tallest the centred box may grow — leaves the chat band readable beneath.</summary>
    internal const int MaxHeight = 24;

    /// <summary>Smallest useful box (header + separators + hint + one row).</summary>
    internal const int MinHeight = 5;

    private readonly CellForgeJumpPalettePanel _panel;
    private PanelContext _ctx = new(new UiState(), 0, 0);
    private Rect _viewport = new(0, 0, 80, 24);
    private IReadOnlyList<string> _rows = Array.Empty<string>();

    /// <summary>Wrap a palette provider (the host owns the instance and its model).</summary>
    public CellForgeJumpPaletteOverlayLayer(CellForgeJumpPalettePanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public Rect Bounds
    {
        get
        {
            int height = Math.Clamp(_rows.Count, MinHeight, MaxHeight);
            return OverlayPopup.CenteredRect(_viewport, BoxWidth(), height);
        }
    }

    /// <summary>
    ///     Box width in columns — independent of the row count, so
    ///     <see cref="Sync" /> can build the provider at its final width before
    ///     the height is known. <see cref="OverlayPopup.CenteredRect" /> clamps
    ///     it to the viewport.
    /// </summary>
    private int BoxWidth() => Math.Min(MaxWidth, _panel.DefaultSize);

    /// <inheritdoc />
    public bool Visible
    {
        get
        {
            if (!IsOpenIn(_ctx.State))
            {
                // Closed palette: no box, no paint, and — critically — no modal
                // barrier, so the composer and panels get their keys back.
                return false;
            }

            var box = Bounds;
            return box.Width > 0 && box.Height > 0;
        }
    }

    /// <summary>Opaque inside the box (the provider blanks it), transparent elsewhere.</summary>
    public bool Opaque => true;

    /// <inheritdoc />
    public bool HitTransparent => false;

    /// <summary>
    ///     Modal input barrier: while the palette is up, keys beneath it
    ///     (panels, composer) starve — the palette owns the keyboard.
    /// </summary>
    public bool IsModal => true;

    /// <summary>
    ///     Refreshes the viewport the centred box is computed from and the
    ///     per-frame provider context, then snapshots the provider's rows (the
    ///     ENG12 replace-wholesale pattern the dock leaves use — one build per
    ///     frame, never one per <see cref="Bounds" /> read). Call once per frame
    ///     (or on resize).
    /// </summary>
    public void Sync(Rect viewport, PanelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _viewport = viewport;
        _ctx = context;
        if (!IsOpenIn(context.State))
        {
            _rows = Array.Empty<string>();
            return;
        }

        // Build at the box width so the provider clips rows with its ellipsis
        // rule instead of a raw slice; the height comes from the row count.
        _rows = CellForgePanelAdapter.RenderToRows(
            _panel,
            context with
            {
                Width = Math.Max(1, Math.Min(BoxWidth(), viewport.Width)),
                Height = Math.Max(1, viewport.Height),
            });
    }

    /// <summary>
    ///     The palette is open when its panel is anything but
    ///     <see cref="TuiPanelState.Hidden" /> in the snapshot — the same
    ///     reducer-driven signal the dock uses, so Ctrl+J and Esc keep toggling
    ///     it through <c>AppMsg.TogglePanel</c>.
    /// </summary>
    private static bool IsOpenIn(UiState state) =>
        state.Ui.PanelStates.TryGetValue(OverlayIds.JumpPalette, out var panelState)
        && panelState != TuiPanelState.Hidden;

    /// <summary>
    ///     Routes a decoded key to the palette. Returns false when the palette
    ///     does not consume it (e.g. Ctrl/Alt chords) so the host keymap still
    ///     sees it.
    /// </summary>
    public bool OnKey(in KeyEvent key)
    {
        if (!Visible || !KeyEventMapper.TryMap(in key, out var dto))
        {
            return false;
        }

        var box = Bounds;
        return _panel.OnKey(
            KeyEventAdapter.ToUiKey(dto),
            _ctx with { Width = Math.Max(1, box.Width), Height = Math.Max(1, box.Height) });
    }

    /// <summary>
    ///     Paints the snapshotted rows into the centred box (blanked first, so
    ///     the chat underneath never bleeds through). Rows past the box height
    ///     are dropped; the provider already clipped them to its width.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!Visible)
        {
            return;
        }

        var box = Bounds;
        if (box.Intersect(clip).Area == 0)
        {
            return;
        }

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
