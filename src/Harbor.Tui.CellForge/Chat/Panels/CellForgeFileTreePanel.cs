using System.Collections.Immutable;
using System.IO;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native file-tree panel: read-only tree of the working directory,
///     directories first. <c>j</c> / <c>k</c> move through the visible rows,
///     <c>h</c> goes to the parent, <c>r</c> refreshes. Since #1487 <c>Enter</c>
///     expands/collapses a directory <em>in place</em> (the current directory does
///     not change); for a file it still dispatches
///     <c>AppMsg.KeyInput(Submit)</c> (the host slash handler routes it to the
///     <c>read</c> tool). Guides mark depth, long names are cut with ellipsis, and
///     a bounded walk with a symlink cycle guard keeps the tree finite.
/// </summary>
/// <remarks>
///     <para>
///         <b>This type performs no I/O, and that is the whole point of #667.</b>
///         It used to call <c>Directory.EnumerateDirectories</c> /
///         <c>EnumerateFiles</c> and read <c>FileAttributes</c> from
///         <see cref="Build" /> — inside a painted frame. The reason it had to
///         was structural, not accidental: the listing lived in a private field,
///         and a render-thread cache can only be filled by the render thread. All
///         three layers had to move together before the call could leave:
///     </para>
///     <list type="number">
///         <item><description>
///             the entries now live in <c>UiState.Ui.FileTrees</c>
///             (<see cref="FileTreeSnapshot" />), written by the reducer from a
///             message and read here;
///         </description></item>
///         <item><description>
///             the walk lives behind the Domain <c>IDirectoryLister</c> port, whose
///             only job is to be slow somewhere other than here;
///         </description></item>
///         <item><description>
///             <c>FileTreeLoader</c> owns the <c>CancellationTokenSource</c> that
///             can actually stop one, so a superseded walk cannot repaint a
///             directory the user already left.
///         </description></item>
///     </list>
///     <para>
///         <b>What Build still does: it asks.</b> One call to
///         <c>ctx.Deps.FileTrees.Request</c>, which starts a cancellable walk off
///         the render thread and returns immediately, and is a no-op once the
///         store has settled this directory. That is not the old defect wearing a
///         new hat — the walk, the stat calls and the sorting are all off-thread
///         now — but it IS a side effect in a function whose whole contract is
///         "read state, return rows", and it is worth being explicit about why it
///         is here rather than pretending it is not: see <c>FileTreeLoader</c>'s
///         remarks on why the view asks instead of subscribing.
///     </para>
///     <para>
///         <b>Cursor and directory remain in the store</b> (<c>PanelCursors</c> /
///         <c>PanelDirs</c>, FP-005/TEA, #360); <see cref="OnKey" /> folds moves
///         through <c>ctx.Deps.Store</c> and never mutates anything itself. The
///         fallback fields cover only the null-store degraded path (tests), which
///         is why they are still here and why the lock still exists.
///     </para>
/// <para>
///     <b>#1487 adds in-place expansion, not a new axis.</b> The loader fetches a
///     small nested tree through the same <c>IDirectoryLister</c> port; Build
///     flattens only the expanded branches and draws guides. Which nodes are open
///     lives in <c>UiState.Ui.PanelExpandedDirs</c> beside the cursor and directory,
///     so it survives repaints and <c>r</c> without any new registry, provider or
///     plugin seam. The walk is depth- and count-bounded and tracks visited paths,
///     so a symlink loop cannot hang it.
/// </para>
/// </remarks>
public sealed class CellForgeFileTreePanel : CellForgePanelBase
{
    private readonly object _gate = new();
    private int _fallbackCursor;
    private string _fallbackDir = string.Empty;

    /// <inheritdoc />
    public override string Id => "file-tree";

    /// <inheritdoc />
    public override string Title => "File Tree";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public override int DefaultSize => 32;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(ctx);
        string dir = ctx.State.Ui.ResolvePanelDirectory(Id);

        // The demand signal. Non-blocking, idempotent, and the only call in this
        // method that is not a pure read of ctx.State.
        ctx.Deps.FileTrees?.Request(Id, dir, ctx.Deps.Store);

        // Covers(dir) is what makes a late result harmless: a snapshot for some
        // other directory answers as "nothing loaded" instead of painting.
        FileTreeSnapshot snapshot = ctx.State.Ui.FileTreeFor(Id, dir);
        List<VisibleNode> nodes = Flatten(ctx, snapshot.Entries);
        int cursor = ResolveCursor(ctx, nodes.Count);
        cursor = nodes.Count == 0 ? 0 : Math.Clamp(cursor, 0, nodes.Count - 1);

        var rows = new List<PanelRows.FileTreeRow>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            VisibleNode node = nodes[i];
            rows.Add(new PanelRows.FileTreeRow(
                node.Entry.Name,
                node.Entry.IsDirectory,
                node.Entry.IsHidden,
                node.Guide,
                node.Expanded));
        }

        List<string> built = PanelRows.FileTreeRows(
            dir,
            snapshot,
            rows,
            cursor,
            ctx.Width,
            ctx.Height);
        return PanelText.Clip(built, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.Enter
            || (key.Code == UiKeyCode.Char && (key.Character == '\r' || key.Character == '\n')))
        {
            string dir = ctx.State.Ui.ResolvePanelDirectory(Id);
            FileTreeSnapshot snapshot = ctx.State.Ui.FileTreeFor(Id, dir);
            List<VisibleNode> nodes = Flatten(ctx, snapshot.Entries);
            int cursor = ResolveCursor(ctx, nodes.Count);

            VisibleNode? current = cursor >= 0 && cursor < nodes.Count ? nodes[cursor] : null;

            if (current is not null)
            {
                if (current.Entry.IsDirectory)
                {
                    // #1487: Enter expands/collapses in place. The current directory
                    // does not change — only which loaded children are visible, and
                    // that choice lives in the store (PanelExpandedDirs).
                    if (ctx.Deps.Store is { } store)
                    {
                        _ = store.Dispatch(new AppMsg.ToggleFileTreeExpansion(Id, current.Entry.FullPath));
                    }
                }
                else if (ctx.Deps.Store is { } store)
                {
                    _ = store.Dispatch(new AppMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
                }
            }

            return true;
        }

        if (key.Code != UiKeyCode.Char || key.Character is null)
        {
            return false;
        }

        // #833: a chord is not a cursor move. This panel holds no text, so it has
        // no claim on a modified rune — Ctrl+j belongs to the host, which is also
        // why it must not silently descend a directory and swallow it. Shift is
        // admitted: the rows below bind 'J', 'K', 'H' and 'R' explicitly.
        if (key.Mods.IsCommandModifier())
        {
            return false;
        }

        switch (key.Character)
        {
            case 'j':
            case 'J':
                return MoveCursor(ctx, +1);

            case 'k':
            case 'K':
                return MoveCursor(ctx, -1);

            case 'h':
            case 'H':
            {
                // Path.GetDirectoryName, not Directory.GetParent: the latter
                // allocates a DirectoryInfo, and every System.IO.Directory* call
                // in this file is exactly what
                // PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-DIRECTORIES
                // forbids. This is pure string handling and the rule table says
                // so explicitly. Null at the filesystem root is the answer, not
                // an error.
                string currentDir = ctx.State.Ui.ResolvePanelDirectory(Id);
                if (Path.GetDirectoryName(currentDir) is { Length: > 0 } parent)
                {
                    MoveToDirectory(ctx, parent);
                }

                return true;
            }

            case 'r':
            case 'R':
                // Invalidate rather than "clear my cache": the listing is state
                // now, so a refresh is an ordinary observable reload — the panel
                // really does go back to "(loading…)" instead of pretending the
                // old rows are current.
                if (ctx.Deps.Store is { } store)
                {
                    _ = store.Dispatch(new AppMsg.InvalidateFileTree(Id));
                }

                lock (_gate)
                {
                    _fallbackCursor = 0;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///     <c>j</c>/<c>k</c>: step the cursor by <paramref name="delta" />, clamped
    ///     to the loaded listing. One method for both directions because they
    ///     differed only in the sign, and the duplication had already produced two
    ///     copies of the store lookup that have to stay in step.
    /// </summary>
    private bool MoveCursor(PanelContext ctx, int delta)
    {
        string dir = ctx.State.Ui.ResolvePanelDirectory(Id);
        FileTreeSnapshot snapshot = ctx.State.Ui.FileTreeFor(Id, dir);
        int count = Flatten(ctx, snapshot.Entries).Count;
        int next;
        lock (_gate)
        {
            int current = ResolveCursor(ctx, count);
            next = count > 0
                ? Math.Clamp(current + delta, 0, count - 1)
                : current;
            _fallbackCursor = next;
        }

        if (ctx.Deps.Store is { } store)
        {
            _ = store.Dispatch(new AppMsg.SetPanelCursor(Id, next));
        }

        return true;
    }

    private void MoveToDirectory(PanelContext ctx, string dir)
    {
        lock (_gate)
        {
            _fallbackDir = dir;
            _fallbackCursor = 0;
        }

        if (ctx.Deps.Store is { } store)
        {
            _ = store.Dispatch(new AppMsg.SetPanelDirectory(Id, dir));
        }
    }

    private int ResolveCursor(PanelContext ctx, int entryCount)
    {
        if (ctx.State.Ui.PanelCursors.TryGetValue(Id, out int stored))
        {
            return Math.Max(0, stored);
        }

        lock (_gate)
        {
            return Math.Clamp(_fallbackCursor, 0, Math.Max(0, entryCount - 1));
        }
    }

    /// <summary>One expanded/collapsed node in display order, with its guide prefix (#1487).</summary>
    private sealed record VisibleNode(FileTreeEntry Entry, string Guide, bool Expanded);

    /// <summary>
    ///     Flatten the loaded tree into the rows currently visible. Pure: reads the
    ///     snapshot and the store's expansion set, does no I/O. Children are shown
    ///     only when their parent directory is expanded, so collapsing hides a whole
    ///     subtree without touching the loaded data or the current directory.
    /// </summary>
    private static List<VisibleNode> Flatten(PanelContext ctx, ImmutableArray<FileTreeEntry> entries)
    {
        var nodes = new List<VisibleNode>();
        for (int i = 0; i < entries.Length; i++)
        {
            AddNode(
                nodes,
                entries[i],
                i == entries.Length - 1,
                prefix: string.Empty,
                depth: 0,
                path => ctx.State.Ui.IsFileTreeNodeExpanded(Id, path));
        }

        return nodes;
    }

    /// <summary>Append one node and, when expanded, its loaded descendants.</summary>
    private static void AddNode(
        List<VisibleNode> nodes,
        FileTreeEntry entry,
        bool isLast,
        string prefix,
        int depth,
        Func<string, bool> isExpanded)
    {
        bool expanded = entry.IsDirectory && isExpanded(entry.FullPath);
        // Depth 0 keeps the old flat look; deeper levels get box-drawing guides.
        string guide = depth == 0 ? string.Empty : prefix + (isLast ? "└── " : "├── ");
        nodes.Add(new VisibleNode(entry, guide, expanded));

        if (!expanded)
        {
            return;
        }

        string childPrefix = depth == 0 ? string.Empty : prefix + (isLast ? "    " : "│   ");
        ImmutableArray<FileTreeEntry> children = entry.ChildEntries;
        for (int i = 0; i < children.Length; i++)
        {
            AddNode(nodes, children[i], i == children.Length - 1, childPrefix, depth + 1, isExpanded);
        }
    }
}
