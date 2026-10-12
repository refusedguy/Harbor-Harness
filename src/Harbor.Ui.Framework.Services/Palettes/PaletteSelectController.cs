namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Pure select-list math (steal/opencode #1177, epic #1155): BCL port of
///     <c>packages/tui/src/ui/select-controller.ts</c> — the half of opencode's
///     <c>DialogSelect</c> that never touches rendering. Zero Harbor
///     dependencies, zero rendering: any host (jump palette, command palette,
///     file picker) drives it and paints from its own rows.
/// </summary>
/// <remarks>
///     <para>
///         <b>What was ported, 1:1:</b> <see cref="ReconcileSelection" />,
///         <see cref="MoveSelection" /> (<c>clamp</c> | <c>wrap</c> policy),
///         <see cref="RevealSelectionOffset" />, <see cref="ReconcileSelectionWindow" />
///         and <see cref="MoveSelectionOffset" /> with its hard-coded
///         <see cref="RevealMarginCells" /> (2-cell) margin. The TypeScript is
///         branch-for-branch identical; only the <c>"clamp" | "wrap"</c> string
///         union became <see cref="MovePolicy" />.
///     </para>
///     <para>
///         <b>What was deliberately NOT ported:</b> everything reactive
///         (<c>groupBy</c>, <c>preserveSelection</c>, <c>focusTarget</c>, footer
///         actions with hints) lives in the SolidJS component, not in this
///         module — the Harbor analogs stay where they belong:
///         grouping/headers in the painting host
///         (<see cref="Harbor.Tui.CellForge.Panels.CellForgeJumpPalettePanel" />),
///         preserve-selection in the model it filters
///         (<see cref="WorktreeJumpPaletteModel" />), action hints in the
///         read-only help surface
///         (<see cref="Harbor.Tui.CellForge.Panels.CellForgeHelpPanel" />).
///     </para>
/// </remarks>
public static class PaletteSelectController
{
    /// <summary>How <see cref="MoveSelection" /> steps past a list end.</summary>
    public enum MovePolicy
    {
        /// <summary>Stop at the first/last row (opencode <c>"clamp"</c>).</summary>
        Clamp = 0,

        /// <summary>Wrap to the other end (opencode <c>"wrap"</c> — the dialog-select move policy).</summary>
        Wrap = 1,
    }

    /// <summary>
    ///     Reveal margin in rows (opencode hard-codes <c>2</c>, capped by half
    ///     the viewport: <c>min(2, floor((limit - 1) / 2))</c>).
    /// </summary>
    public const int RevealMarginCells = 2;

    /// <summary>Clamp a selection into <c>[0, count - 1]</c> (<c>0</c> when empty).</summary>
    public static int ReconcileSelection(int selected, int count) =>
        Math.Max(0, Math.Min(count - 1, selected));

    /// <summary>
    ///     Step a selection by <paramref name="delta" />: clamp at the ends, or
    ///     wrap to the other end under <see cref="MovePolicy.Wrap" />.
    ///     Returns <c>0</c> when <paramref name="count" /> is empty.
    /// </summary>
    public static int MoveSelection(int selected, int count, int delta, MovePolicy policy = MovePolicy.Clamp)
    {
        if (count <= 0)
        {
            return 0;
        }

        int next = selected + delta;
        if (policy == MovePolicy.Clamp)
        {
            return ReconcileSelection(next, count);
        }

        if (next < 0)
        {
            return count - 1;
        }

        if (next >= count)
        {
            return 0;
        }

        return next;
    }

    /// <summary>
    ///     Offset that keeps <paramref name="selected" /> inside an
    ///     <paramref name="offset" />…<c>offset + limit - 1</c> window,
    ///     clamped to <c>[0, count - limit]</c>.
    /// </summary>
    public static int RevealSelectionOffset(int offset, int count, int limit, int selected)
    {
        int max = MaxOffset(count, limit);
        if (selected < offset)
        {
            return Math.Min(max, selected);
        }

        if (selected >= offset + limit)
        {
            return Math.Min(max, selected - limit + 1);
        }

        return Math.Max(0, Math.Min(max, offset));
    }

    /// <summary>
    ///     Selection reconciled into the visible
    ///     <paramref name="offset" />…<c>offset + limit - 1</c> window
    ///     (<c>0</c> when <paramref name="count" /> is empty).
    /// </summary>
    public static int ReconcileSelectionWindow(int selected, int count, int limit, int offset)
    {
        if (count <= 0)
        {
            return 0;
        }

        int first = ReconcileSelection(offset, count);
        int last = Math.Min(count - 1, first + Math.Max(1, limit) - 1);
        return Math.Max(first, Math.Min(last, selected));
    }

    /// <summary>
    ///     Scroll offset following a <paramref name="direction" /> step
    ///     (<c>-1</c> up, <c>+1</c> down): the window only moves once the
    ///     selection crosses the <see cref="RevealMarginCells" /> margin, so
    ///     the cursor never hugs the viewport edge.
    /// </summary>
    public static int MoveSelectionOffset(int offset, int count, int limit, int selected, int direction)
    {
        int max = MaxOffset(count, limit);
        int margin = Math.Max(0, Math.Min(RevealMarginCells, (limit - 1) / 2));
        if (direction < 0 && selected < offset + margin)
        {
            return Math.Max(0, Math.Min(max, selected - margin));
        }

        if (direction > 0 && selected > offset + limit - margin - 1)
        {
            return Math.Min(max, selected - limit + margin + 1);
        }

        return Math.Max(0, Math.Min(max, offset));
    }

    private static int MaxOffset(int count, int limit) => Math.Max(0, count - limit);
}
