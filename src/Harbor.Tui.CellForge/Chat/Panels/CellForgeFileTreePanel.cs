using System.Collections.Immutable;
using System.IO;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native file-tree panel: read-only listing of the working directory,
///     directories first. <c>j</c> / <c>k</c> move, <c>h</c> goes to the parent,
///     <c>r</c> refreshes, <c>Enter</c> descends into directories or dispatches
///     <c>AppMsg.KeyInput(Submit)</c> for files (the host slash handler routes it to
///     the <c>read</c> tool).
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
        ArgumentNullException.ThrowIfNull(ctx);
        string dir = ctx.State.Ui.ResolvePanelDirectory(Id);

        // The demand signal. Non-blocking, idempotent, and the only call in this
        // method that is not a pure read of ctx.State.
        ctx.Deps.FileTrees?.Request(Id, dir, ctx.Deps.Store);

        // Covers(dir) is what makes a late result harmless: a snapshot for some
        // other directory answers as "nothing loaded" instead of painting.
        FileTreeSnapshot snapshot = ctx.State.Ui.FileTreeFor(Id, dir);
        ImmutableArray<FileTreeEntry> entries = snapshot.Entries;
        int cursor = ResolveCursor(ctx, entries.Length);
        cursor = entries.Length == 0 ? 0 : Math.Clamp(cursor, 0, entries.Length - 1);

        var rows = PanelRows.FileTreeRows(
            dir,
            snapshot,
            ToRows(entries),
            cursor,
            ctx.Width,
            ctx.Height);
        return PanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.Enter
            || (key.Code == UiKeyCode.Char && (key.Character == '\r' || key.Character == '\n')))
        {
            string dir = ctx.State.Ui.ResolvePanelDirectory(Id);
            FileTreeSnapshot snapshot = ctx.State.Ui.FileTreeFor(Id, dir);
            ImmutableArray<FileTreeEntry> entries = snapshot.Entries;
            int cursor = ResolveCursor(ctx, entries.Length);

            FileTreeEntry? current =
                cursor >= 0 && cursor < entries.Length ? entries[cursor] : null;

            if (current is not null)
            {
                if (current.IsDirectory)
                {
                    MoveToDirectory(ctx, current.FullPath);
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

        switch (key.Character)
        {
            case 'j':
            case 'J':
            {
                string dir = ctx.State.Ui.ResolvePanelDirectory(Id);
                int count = ctx.State.Ui.FileTreeFor(Id, dir).Entries.Length;
                int next;
                lock (_gate)
                {
                    int current = ResolveCursor(ctx, count);
                    next = count > 0
                        ? Math.Min(count - 1, current + 1)
                        : current;
                    _fallbackCursor = next;
                }

                if (ctx.Deps.Store is { } store)
                {
                    _ = store.Dispatch(new AppMsg.SetPanelCursor(Id, next));
                }

                return true;
            }

            case 'k':
            case 'K':
            {
                string dir = ctx.State.Ui.ResolvePanelDirectory(Id);
                int count = ctx.State.Ui.FileTreeFor(Id, dir).Entries.Length;
                int next;
                lock (_gate)
                {
                    int current = ResolveCursor(ctx, count);
                    next = count > 0
                        ? Math.Max(0, current - 1)
                        : current;
                    _fallbackCursor = next;
                }

                if (ctx.Deps.Store is { } store)
                {
                    _ = store.Dispatch(new AppMsg.SetPanelCursor(Id, next));
                }

                return true;
            }

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

    /// <summary>
    ///     Project the state entries into the projection's row vocabulary. The
    ///     allocation is per frame and per entry, which is why the file tree caps
    ///     what it loads: a bounded list of small strings is a fine thing to build
    ///     every frame, an unbounded directory walk is not.
    /// </summary>
    private static List<PanelRows.FileTreeRow> ToRows(ImmutableArray<FileTreeEntry> entries)
    {
        var rows = new List<PanelRows.FileTreeRow>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            rows.Add(new PanelRows.FileTreeRow(entries[i].Name, entries[i].IsDirectory, entries[i].IsHidden));
        }

        return rows;
    }
}
