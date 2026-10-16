using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One declarative screen leaf (epic #1195, part of #1201): an id, a
/// <see cref="FlexTrack"/> describing its share of the parent extent, and the
/// factory that creates the leaf panel. Sources: demo <c>ui.rs</c> / demo2
/// <c>app.rs</c> (vertical <c>[Length(1), Min(0), Length(1)]</c> —
/// title/tabs/bottom) and the todo-list 4-piece screen. The chat screen's
/// manual <c>ChatScreen.Build</c> assembly plus
/// <c>ChatScreenPanelDock.AttachPanels</c> ratio math are the in-repo
/// antecedent this generalizes.
/// </summary>
public sealed class ScreenLeafSpec
{
    /// <summary>Layout-tree id (must be unique within the screen).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Share of the parent extent (ratatui <c>Constraint</c> analogue).
    /// Carried for the future <see cref="FlexLayout"/> pass; the current
    /// solver still consumes the split ratios in <see cref="ScreenSplitSpec"/>,
    /// so declaring a track never changes a solved frame.
    /// </summary>
    public FlexTrack Track { get; init; } = FlexTrack.Fill();

    /// <summary>Factory creating the leaf panel (chat, dock, or generic).</summary>
    public required Func<Panel> Create { get; init; }
}

/// <summary>
/// One declarative split (epic #1195, part of #1201): retargets the leaf named
/// by <see cref="TargetId"/> the way <see cref="LayoutTree.Split"/> does,
/// seating <see cref="NewPanelId"/> beside it. Both ids must name entries of
/// the leaf list passed to <see cref="ScreenBuilder.Build"/>.
/// </summary>
public sealed class ScreenSplitSpec
{
    /// <summary>Leaf to split (must already be seated).</summary>
    public required string TargetId { get; init; }

    /// <summary>Split axis.</summary>
    public SplitDir Direction { get; init; } = SplitDir.Vertical;

    /// <summary>Share of the usable extent kept by the target side.</summary>
    public float Ratio { get; init; } = 0.5f;

    /// <summary>Leaf to seat beside the target (must name a leaf entry).</summary>
    public required string NewPanelId { get; init; }

    /// <summary>Gap cells between the sides.</summary>
    public byte Gap { get; init; }
}

/// <summary>
/// Declarative screen builder (epic #1195, part of #1201): a leaf
/// (id, track, leaf-factory) list plus splits, assembled through
/// <see cref="LayoutTree.AddRoot"/> / <see cref="LayoutTree.Split"/>.
/// Pure setup-time work (no buffer writes, no store access) so tests assert
/// the tree shape without a terminal. Paint path untouched: every solved
/// frame is byte-identical to the equivalent manual <c>AddRoot</c> + Aggregate
/// with NaN ratios degrading to the even split instead of poisoning the
/// solver.
/// </summary>
public static class ScreenBuilder
{
    /// <summary>
    /// Builds the layout tree from leaves plus splits. The first leaf is the
    /// root; each split seats one more leaf beside its target.
    /// </summary>
    /// <param name="leaves">Leaves in seating order (at least one).</param>
    /// <param name="splits">Splits seating leaves 1..N (one per extra leaf by default).</param>
    public static LayoutTree Build(
        IReadOnlyList<ScreenLeafSpec> leaves,
        IReadOnlyList<ScreenSplitSpec>? splits = null)
    {
        Ensure.NotNull(leaves);
        if (leaves.Count == 0)
        {
            throw new ArgumentException("At least one leaf is required.", nameof(leaves));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            Ensure.NotNull(leaf);
            Ensure.NotBlank(leaf.Id);
            Ensure.NotNull(leaf.Create);
            if (!seen.Add(leaf.Id))
            {
                throw new ArgumentException($"Duplicate leaf id '{leaf.Id}'.", nameof(leaves));
            }
        }

        splits ??= Array.Empty<ScreenSplitSpec>();
        var byId = new Dictionary<string, ScreenLeafSpec>(StringComparer.Ordinal);
        for (int i = 0; i < leaves.Count; i++)
        {
            byId[leaves[i].Id] = leaves[i];
        }

        var tree = new LayoutTree();
        tree.AddRoot(leaves[0].Create());

        for (int i = 0; i < splits.Count; i++)
        {
            var split = splits[i];
            Ensure.NotNull(split);
            Ensure.NotBlank(split.TargetId);
            Ensure.NotBlank(split.NewPanelId);
            if (!byId.TryGetValue(split.NewPanelId, out var spec))
            {
                throw new ArgumentException($"Split seats unknown leaf '{split.NewPanelId}'.", nameof(splits));
            }

            float ratio = float.IsNaN(split.Ratio) ? 0.5f : Math.Clamp(split.Ratio, 0.05f, 0.95f);
            tree.Split(split.TargetId, split.Direction, ratio, spec.Create(), split.Gap);
        }

        return tree;
    }

    /// <summary>
    /// Generalized dock attach (epic #1195, part of #1201): seats
    /// <paramref name="leaf"/> beside the <paramref name="anchorId"/> leaf,
    /// reserving <paramref name="sizeCells"/> rows (vertical) or columns
    /// (horizontal) for it. The ratio math is the single home for what used to
    /// live twice in <c>ChatScreenPanelDock.AttachBottom</c> (rows, gap 0) and
    /// <c>AttachSide</c> (columns, 1-column gap): the anchor keeps
    /// <c>usable − min(size, usable − 1)</c> of the gap-subtracted extent,
    /// clamped to 5%..95% so a degenerate size degrades to a sliver instead of
    /// collapsing the anchor. The tree is left unsolved; the caller solves.
    /// </summary>
    /// <param name="tree">Tree containing the anchor leaf.</param>
    /// <param name="anchorId">Leaf to split (stays on the A side).</param>
    /// <param name="dir">Axis to share (Vertical = rows, Horizontal = columns).</param>
    /// <param name="leaf">Leaf to seat on the B side.</param>
    /// <param name="sizeCells">Rows/columns the new leaf reserves.</param>
    /// <param name="gap">Gap cells between the sides.</param>
    public static void AttachLeaf(
        LayoutTree tree,
        string anchorId,
        SplitDir dir,
        Panel leaf,
        int sizeCells,
        byte gap = 0)
    {
        Ensure.NotNull(tree);
        Ensure.NotBlank(anchorId);
        Ensure.NotNull(leaf);

        Panel? anchor = null;
        foreach (var panel in tree.Panels)
        {
            if (panel.Id == anchorId)
            {
                anchor = panel;
                break;
            }
        }

        if (anchor is null)
        {
            throw new KeyNotFoundException($"anchor '{anchorId}' not found");
        }

        int extent = dir == SplitDir.Vertical ? anchor.Rect.Height : anchor.Rect.Width;
        int avail = extent > 0
            ? Math.Max(1, extent)
            : Math.Max(1, sizeCells) + Math.Max(1, leaf.MinAlong(dir));
        int usable = Math.Max(1, avail - gap);
        float ratio = Math.Clamp(
            (float)(usable - Math.Min(Math.Max(1, sizeCells), usable - 1)) / usable,
            0.05f,
            0.95f);
        tree.Split(anchorId, dir, ratio, leaf, gap);
    }
}
