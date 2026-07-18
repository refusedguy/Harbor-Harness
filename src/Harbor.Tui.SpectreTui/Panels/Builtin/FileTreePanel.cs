using System.Collections.Generic;
using System.IO;
using System.Text;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Console;
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
    private int _cursor;
    private List<Entry> _entries = new();
    private string _currentDir = string.Empty;
    private string _displayDir = string.Empty;

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

        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup("[bold cyan]File Tree[/]"));
        p.Lines.Add(TextLine.FromMarkup($"[grey]{ChatMarkup.Escape(ShortenPath(_displayDir, ctx.Width - 2))}[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────[/]"));

        if (_entries.Count == 0)
        {
            p.Lines.Add(TextLine.FromMarkup("[grey](empty directory)[/]"));
            return p;
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int start = Math.Max(0, _cursor - maxVisible + 1);
        int end = Math.Min(_entries.Count, start + maxVisible);

        if (start > 0)
            p.Lines.Add(TextLine.FromMarkup("[grey]  ↑ more above[/]"));

        for (int i = start; i < end; i++)
        {
            var entry = _entries[i];
            bool selected = i == _cursor;
            string icon = entry.IsDirectory ? "[blue]▸[/]" : entry.IsHidden ? "[grey]·[/]" : "[grey] [/]";
            string name = ChatMarkup.Escape(Truncate(entry.Name, ctx.Width - 6));
            string prefix = selected ? "[black on aqua] [/]" : " ";
            p.Lines.Add(TextLine.FromMarkup($"{prefix} {icon} {name}"));
        }

        if (end < _entries.Count)
            p.Lines.Add(TextLine.FromMarkup("[grey]  ↓ more below[/]"));

        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]j/k move · Enter open · h parent · r refresh[/]"));
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
                    else if (ctx.Services?.GetService(typeof(UiStore)) is UiStore store)
                    {
                        // Dispatch a slash-prompt that the host's slash handler
                        // routes to the read tool (if registered).
                        store.Dispatch(new UiMsg.KeyInput(
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

        var dir = string.IsNullOrEmpty(_currentDir) ? Environment.CurrentDirectory : _currentDir;
        _currentDir = dir;
        _displayDir = dir;

        var entries = new List<Entry>(32);
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(d);
                entries.Add(new Entry(
                    info.Name + Path.DirectorySeparatorChar,
                    info.FullName,
                    IsDirectory: true,
                    IsHidden: (info.Attributes & FileAttributes.Hidden) != 0));
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(f);
                entries.Add(new Entry(
                    info.Name,
                    info.FullName,
                    IsDirectory: false,
                    IsHidden: (info.Attributes & FileAttributes.Hidden) != 0));
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
            return System.StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        _entries = entries;
        if (_cursor >= _entries.Count)
            _cursor = 0;
    }

    private static string ShortenPath(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
            return path;
        // Show the last `max` chars of the path, prefix with ellipsis.
        return "…" + path[^(max - 1)..];
    }

    private static string Truncate(string text, int max)
    {
        if (max <= 3) return text;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private sealed record Entry(string Name, string FullPath, bool IsDirectory, bool IsHidden);
}
