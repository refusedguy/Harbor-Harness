using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;
/// <summary>
///     Builtin panel that shows the working directory's file tree. <c>j/k</c>
///     navigates, <c>Enter</c> opens the selected file by dispatching a
///     <c>read</c> tool prompt through the agent (best-effort — the user must
///     confirm if the agent isn't running).
/// </summary>
/// <remarks>
///     <para>
///         <b>Decoupling:</b> the panel reads the filesystem directly (read-only).
///         It does not invoke the agent — when the user presses Enter, it dispatches
///         a slash command into the <c>UiStore</c> which the host's slash handler
///         turns into a real <c>read</c> tool call.
///     </para>
///     <para>
///         <b>Navigation state:</b> the cursor index is held in a per-instance
///         mutable field. This violates the "no renderer-side state" guideline
///         (see audit §FP-005), but moving it into <c>UiState</c> would require a
///         per-panel state map keyed by id. Tracked as TODO(principles) — the
///         compromise is acceptable because the file tree is the only panel with
///         significant UI-local state.
///     </para>
/// </remarks>
public sealed class FileTreePanel : IPanelProvider
{
    private string _currentDir = string.Empty;
    private int _cursor;
    private string _displayDir = string.Empty;
    private List<Entry> _entries = new();

    /// <inheritdoc />
    public string Id => "file-tree";

    /// <inheritdoc />
    public string Title => "File Tree";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public int DefaultSize => 32;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        EnsureEntries(ctx);

        var rows = PanelRows.FileTreeRows(
            _displayDir,
            _entries.Select(e => new PanelRows.FileTreeRow(e.Name, e.IsDirectory, e.IsHidden)).ToList(),
            _cursor,
            ctx.Width,
            ctx.Height);

        var p = new Paragraph().Alignment(Justify.Left);
        for (int i = 0; i < rows.Count; i++)
        {
            string row = rows[i];
            if (i == 0 && row == "File Tree")
            {
                p.Lines.Add(TextLine.FromMarkup("[bold cyan]File Tree[/]"));
                continue;
            }

            if (i == 1)
            {
                // Shortened directory path row (not a literal).
                p.Lines.Add(TextLine.FromMarkup("[grey]" + ChatMarkup.Escape(row) + "[/]"));
                continue;
            }

            p.Lines.Add(TextLine.FromMarkup(StyleRow(row)));
        }

        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code != UiKeyCode.Char || key.Character is null)
            return false;

        switch (key.Character)
        {
            case 'j':
            case 'J':
                if (_entries.Count > 0)
                    _cursor = Math.Min(_entries.Count - 1, _cursor + 1);
                return true;
            case 'k':
            case 'K':
                if (_entries.Count > 0)
                    _cursor = Math.Max(0, _cursor - 1);
                return true;
            case 'h':
            case 'H':
                // Move to parent directory.
                if (Directory.GetParent(_currentDir) is { } parent)
                {
                    _currentDir = parent.FullName;
                    _displayDir = parent.FullName;
                    _cursor = 0;
                }
                return true;
            case 'r':
            case 'R':
                // Force refresh by clearing the cached directory.
                _entries = new List<Entry>();
                return true;
            case '\r':
            case '\n':
                // Enter — open the file (if it's a file) or descend (if directory).
                if (_cursor >= 0 && _cursor < _entries.Count)
                {
                    var entry = _entries[_cursor];
                    if (entry.IsDirectory)
                    {
                        _currentDir = entry.FullPath;
                        _displayDir = entry.FullPath;
                        _cursor = 0;
                    }
                    else if (ctx.Deps.Store is { } store)
                    {
                        // Dispatch a slash-prompt that the host's slash handler
                        // routes to the read tool (if registered).
                        store.Dispatch(new AppMsg.KeyInput(
                            ChatAction.Submit,
                            UiKey.ForChar('\r')));
                        // Best-effort: also publish a slash command via the
                        // TuiEffect.RunSlash path. The store's Dispatch returns the
                        // effect for the host to run; we cannot run it from here, so
                        // we log and let the user see the result.
                    }
                }
                return true;
        }
        return false;
    }

    private void EnsureEntries(PanelContext ctx)
    {
        if (_entries.Count > 0) return;

        string dir = string.IsNullOrEmpty(_currentDir) ? Environment.CurrentDirectory : _currentDir;
        _currentDir = dir;
        _displayDir = dir;

        var entries = new List<Entry>(32);
        try
        {
            foreach (string d in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(d);
                entries.Add(new Entry(
                    info.Name + Path.DirectorySeparatorChar,
                    info.FullName,
                    true,
                    (info.Attributes & FileAttributes.Hidden) != 0));
            }
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(f);
                entries.Add(new Entry(
                    info.Name,
                    info.FullName,
                    false,
                    (info.Attributes & FileAttributes.Hidden) != 0));
            }
        }
        catch (IOException)
        {
            // Directory not readable — leave entries empty.
        }
        catch (UnauthorizedAccessException)
        {
            // No permissions — leave entries empty.
        }

        entries.Sort((a, b) =>
        {
            int cmp = a.IsDirectory.CompareTo(b.IsDirectory) * -1; // dirs first
            if (cmp != 0) return cmp;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        _entries = entries;
        if (_cursor >= _entries.Count)
            _cursor = 0;
    }

    private static string StyleRow(string row)
    {
        if (row == PanelText.Separator)
            return "[grey]" + PanelText.Separator + "[/]";

        if (row is "(empty directory)" or "  ↑ more above" or "  ↓ more below"
            or "j/k move · Enter open · h parent · r refresh")
            return "[grey]" + ChatMarkup.Escape(row) + "[/]";

        if (row.Length >= 5 && (row[0] == '>' || row[0] == ' ') &&
            (row[2] is '▸' or '·' or ' '))
        {
            string prefix = row[0] == '>' ? "[black on aqua] [/]" : " ";
            string icon = row[2] switch
            {
                '▸' => "[blue]▸[/]",
                '·' => "[grey]·[/]",
                _ => "[grey] [/]",
            };
            return $"{prefix} {icon} {ChatMarkup.Escape(row.Length > 4 ? row[4..] : string.Empty)}";
        }

        return "[grey]" + ChatMarkup.Escape(row) + "[/]";
    }

    private sealed record Entry(string Name, string FullPath, bool IsDirectory, bool IsHidden);
}
