using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native file-tree panel: read-only listing of the working directory,
///     directories first. <c>j</c> / <c>k</c> move, <c>h</c> goes to the parent,
///     <c>r</c> refreshes, <c>Enter</c> descends into directories or dispatches
///     <c>UiMsg.KeyInput(Submit)</c> for files (the host slash handler routes it to
///     the <c>read</c> tool).
/// </summary>
/// <remarks>
///     TODO(principles)[FP-005, TEA]: cursor + directory cache are provider-local
///     mutable state (same compromise as the Spectre original) instead of living in
///     <see cref="UiState"/> keyed by panel id. Guarded by a small lock so
///     <c>Build</c> (render thread) and <c>OnKey</c> (input thread) stay thread-safe;
///     moving the cursor into the store is follow-up work. Tracked in #360.
/// </remarks>
public sealed class CellForgeFileTreePanel : CellForgePanelBase
{
    private readonly object _gate = new();
    private string _currentDir = string.Empty;
    private int _cursor;
    private string _displayDir = string.Empty;
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
        EnsureEntries();
        List<Entry> snapshot;
        int cursor;
        string displayDir;
        lock (_gate)
        {
            snapshot = new List<Entry>(_entries);
            cursor = _cursor;
            displayDir = _displayDir;
        }

        var rows = PanelRows.FileTreeRows(
            displayDir,
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
                current = _cursor >= 0 && _cursor < _entries.Count ? _entries[_cursor] : null;
            }

            if (current is not null)
            {
                if (current.IsDirectory)
                {
                    lock (_gate)
                    {
                        _currentDir = current.FullPath;
                        _displayDir = current.FullPath;
                        _entries = new List<Entry>();
                        _cursor = 0;
                    }
                }
                else if (ctx.Store is UiStore store)
                {
                    _ = store.Dispatch(new UiMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
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
                lock (_gate)
                {
                    if (_entries.Count > 0)
                    {
                        _cursor = Math.Min(_entries.Count - 1, _cursor + 1);
                    }
                }

                return true;
            case 'k':
            case 'K':
                lock (_gate)
                {
                    if (_entries.Count > 0)
                    {
                        _cursor = Math.Max(0, _cursor - 1);
                    }
                }

                return true;
            case 'h':
            case 'H':
                string currentDir;
                lock (_gate)
                {
                    currentDir = string.IsNullOrEmpty(_currentDir) ? Environment.CurrentDirectory : _currentDir;
                }

                if (Directory.GetParent(currentDir) is { } parent)
                {
                    lock (_gate)
                    {
                        _currentDir = parent.FullName;
                        _displayDir = parent.FullName;
                        _entries = new List<Entry>();
                        _cursor = 0;
                    }
                }

                return true;
            case 'r':
            case 'R':
                lock (_gate)
                {
                    _entries = new List<Entry>();
                }

                return true;
            default:
                return false;
        }
    }

    private void EnsureEntries()
    {
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                return;
            }
        }

        string dir;
        lock (_gate)
        {
            dir = string.IsNullOrEmpty(_currentDir) ? Environment.CurrentDirectory : _currentDir;
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
            if (_entries.Count == 0)
            {
                _entries = fresh;
                _currentDir = dir;
                _displayDir = dir;
                if (_cursor >= _entries.Count)
                {
                    _cursor = 0;
                }
            }
        }
    }

    private sealed record Entry(string Name, string FullPath, bool IsDirectory, bool IsHidden);
}
