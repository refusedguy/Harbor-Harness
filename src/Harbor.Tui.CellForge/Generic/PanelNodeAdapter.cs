// #2688 (tui-composition-2026-11, idea 1): a host-owned Panel cannot enter the
// engine's measure/arrange/prepaint/paint pipeline on its own. The two layers
// keep separate cell vocabularies (#436), so this adapter is the one place that
// knows both. It also carries the panel's Id as a named ElementId step: a
// container keyed by PanelId keeps prepaint records and element state attached
// to the panel instead of to a slot when siblings are reordered (idea 2).

using Harbor.Tui.CellForge.Rendering;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>
/// Wraps a layout-tree <see cref="Panel"/> as an engine <see cref="EngineCells.INode"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PanelId"/> is the stable key a container passes to a keyed
/// <see cref="EngineCells.FlexSplitNode"/> slot, so the child id becomes
/// <c>/parent/panelId</c> rather than a position. The
/// prepaint version key folds the same id in, so a record committed for one
/// panel cannot be reused for a different panel that happens to take its slot.
/// </para>
/// <para>
/// Paint is a member-by-member bridge like <c>GenericTuiRenderer.PaintRoot</c>:
/// the panel draws into a shared rendering-layer grid, which is copied into the
/// engine grid one cell at a time. No numeric cell/enum cast crosses the boundary.
/// </para>
/// </remarks>
internal sealed class PanelNodeAdapter : EngineCells.INode
{
    private readonly Panel _panel;
    private ScreenBuffer? _panelBuffer;
    private EngineCells.Rect _arranged;

    /// <summary>Creates an adapter over <paramref name="panel"/>.</summary>
    /// <param name="panel">Panel to host in the node pipeline.</param>
    internal PanelNodeAdapter(Panel panel) => _panel = Ensure.NotNull(panel);

    /// <summary>Stable key for a keyed container slot.</summary>
    internal string PanelId => _panel.Id;

    /// <summary>The named element id the panel keeps across sibling reorders.</summary>
    internal EngineCells.ElementId NamedId => EngineCells.ElementId.Root.Child(_panel.Id);

    /// <inheritdoc />
    public EngineCells.Rect Arranged => _arranged;

    // Folding the id into the key makes the identity observable: a different
    // panel in the same slot carries a different key and cannot reuse a record.
    private ulong PrepaintKey => (ulong)(uint)StringComparer.Ordinal.GetHashCode(_panel.Id);

    /// <inheritdoc />
    public EngineCells.Size Measure(EngineCells.Constraints constraints) =>
        new(
            Math.Min(Math.Max(0, _panel.Min.Width), constraints.Width),
            Math.Min(Math.Max(0, _panel.Min.Height), constraints.Height));

    /// <inheritdoc />
    public void Arrange(EngineCells.Rect rect)
    {
        _arranged = new EngineCells.Rect(
            Math.Max(0, rect.X),
            Math.Max(0, rect.Y),
            Math.Max(0, rect.Width),
            Math.Max(0, rect.Height));
        _panel.Rect = new Rect(_arranged.X, _arranged.Y, _arranged.Width, _arranged.Height);
    }

    /// <inheritdoc />
    public EngineCells.PrepaintRecord Prepaint(EngineCells.PrepaintContext context, EngineCells.ElementId id)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryReuse(id, PrepaintKey, out var reused)
            ? reused
            : context.Commit(id, PrepaintKey, EngineCells.PrepaintRecord.Empty with { Bounds = _arranged });
    }

    /// <inheritdoc />
    public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _panelBuffer ??= new ScreenBuffer(buffer.Cols, buffer.Rows);
        if (_panelBuffer.Cols != buffer.Cols || _panelBuffer.Rows != buffer.Rows)
        {
            _panelBuffer.Resize(buffer.Cols, buffer.Rows);
        }

        _panelBuffer.BlankAll();
        _panel.Paint(_panelBuffer);

        var area = _arranged.Intersect(clip);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                Cell painted = _panelBuffer.At(x, y);
                buffer.At(x, y) = EngineCells.Cell.FromRaw(
                    painted.Rune, painted.Fg, painted.Bg, painted.Flags, painted.Width);
            }
        }
    }
}
