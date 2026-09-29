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
///     Cursor + current directory live in <see cref="UiState"/> keyed by panel
///     id (<c>PanelCursors</c> / <c>PanelDirs</c>, FP-005/TEA, #360):
///     <c>Build</c> resolves them from <c>ctx.State</c> (missing key = cursor 0 /
///     process working directory) and <c>OnKey</c> folds moves through
///     <c>ctx.Deps.Store</c> via <c>AppMsg.SetPanelCursor</c> /
///     <c>AppMsg.SetPanelDirectory</c> (descend/parent resets the cursor to 0
///     atomically in the reducer). The filesystem listing itself stays a
///     provider-local cache — the reducer must never do I/O — invalidated
///     whenever the resolved directory changes. The small lock (plus the
///     fallback fields for the null-store degraded path) keeps <c>Build</c>
///     (render thread) and <c>OnKey</c> (input thread) thread-safe.
/// </remarks>
public sealed class CellForgeFileTreePanel : CellForgePanelBase
{
    private readonly object _gate = new();
    private int _fallbackCursor;
    private string _fallbackDir = string.Empty;
    private string _entriesDir = string.Empty;
    private List<Entry> _entries = new();

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
        string dir = ResolveDir(ctx);
        EnsureEntries(dir);
        List<Entry> snapshot;
        int cursor;
        lock (_gate)
        {
            snapshot = new List<Entry>(_entries);
            cursor = ResolveCursor(ctx);
        }

        cursor = snapshot.Count == 0 ? 0 : Math.Clamp(cursor, 0, snapshot.Count - 1);

        var rows = PanelRows.FileTreeRows(
            dir,
            snapshot.Select(e => new PanelRows.FileTreeRow(e.Name, e.IsDirectory, e.IsHidden)).ToList(),
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
            Entry? current;
            lock (_gate)
            {
                int cursor = ResolveCursor(ctx);
                current = cursor >= 0 && cursor < _entries.Count ? _entries[cursor] : null;
            }

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
                int next;
                lock (_gate)
                {
                    int current = ResolveCursor(ctx);
                    next = _entries.Count > 0
                        ? Math.Min(_entries.Count - 1, current + 1)
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
                int next;
                lock (_gate)
                {
                    int current = ResolveCursor(ctx);
                    next = _entries.Count > 0
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
                string currentDir = ResolveDir(ctx);
                if (Directory.GetParent(currentDir) is { } parent)
                {
                    MoveToDirectory(ctx, parent.FullName);
                }

                return true;
            }

            case 'r':
            case 'R':
                lock (_gate)
                {
                    _entries = new List<Entry>();
                    _entriesDir = string.Empty;
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
            _entries = new List<Entry>();
            _entriesDir = string.Empty;
        }

        if (ctx.Deps.Store is { } store)
        {
            _ = store.Dispatch(new AppMsg.SetPanelDirectory(Id, dir));
        }
    }

    private int ResolveCursor(PanelContext ctx)
    {
        if (ctx.State.Ui.PanelCursors.TryGetValue(Id, out int stored))
        {
            return Math.Max(0, stored);
        }

        lock (_gate)
        {
            return _fallbackCursor;
        }
    }

    private string ResolveDir(PanelContext ctx)
    {
        if (ctx.State.Ui.PanelDirs.TryGetValue(Id, out string? stored))
        {
            return string.IsNullOrEmpty(stored) ? Environment.CurrentDirectory : stored;
        }

        lock (_gate)
        {
            return string.IsNullOrEmpty(_fallbackDir) ? Environment.CurrentDirectory : _fallbackDir;
        }
    }

    private void EnsureEntries(string dir)
    {
        lock (_gate)
        {
            if (_entries.Count > 0 && _entriesDir == dir)
            {
                return;
            }
        }

        var fresh = new List<Entry>(32);
        try
        {
            foreach (string d in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(d);
                fresh.Add(new Entry(
                    info.Name + Path.DirectorySeparatorChar,
                    info.FullName,
                    true,
                    (info.Attributes & FileAttributes.Hidden) != 0));
            }

            foreach (string f in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(f);
                fresh.Add(new Entry(
                    info.Name,
                    info.FullName,
                    false,
                    (info.Attributes & FileAttributes.Hidden) != 0));
            }
        }
        catch (IOException)
        {
            // Directory not readable — publish an empty listing below.
        }
        catch (UnauthorizedAccessException)
        {
            // No permissions — publish an empty listing below.
        }

        fresh.Sort(static (a, b) =>
        {
            int cmp = b.IsDirectory.CompareTo(a.IsDirectory); // dirs first
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        lock (_gate)
        {
            if (_entries.Count == 0 || _entriesDir != dir)
            {
                _entries = fresh;
                _entriesDir = dir;
                if (_fallbackCursor >= _entries.Count)
                {
                    _fallbackCursor = 0;
                }
            }
        }
    }

    private sealed record Entry(string Name, string FullPath, bool IsDirectory, bool IsHidden);
}
