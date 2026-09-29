using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;

using CSharpFunctionalExtensions;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// One file entry of the file picker: display path plus optional detail,
/// directory flag, and inline preview lines supplied by the host.
/// </summary>
public sealed record FilePickerItem(
    string Path,
    string Detail = "",
    bool IsDirectory = false,
    IReadOnlyList<string>? PreviewLines = null);

/// <summary>
/// Fuzzy file picker overlay (bubbles filepicker pattern): fuzzy-filtered
/// file list with a side preview of the selected entry. Needed by
/// @-mentions/pickers. The view is UI-only — the host supplies
/// <see cref="FilePickerItem" />s (including preview lines) and subscribes
/// via <see cref="OnCommit" />; file-system access stays out of the widget.
/// Paint draws a bordered box; the host decides overlay placement by passing
/// a <see cref="Rect" />. When the box is wide enough the right pane shows
/// the selected entry preview, otherwise the list takes the full width.
/// </summary>
public sealed class FilePickerView
{
    private const int PageRows = 5;

    /// <summary>Max preview body lines kept per item (host may supply more; extras are clipped).</summary>
    public const int MaxPreviewLines = 32;

    private IReadOnlyList<FilePickerItem> _files = [];
    private List<FilePickerItem> _results = [];
    private string _query = string.Empty;
    private int _selected;
    private int _offset;

    /// <summary>Rows the last Paint actually showed — drives list scrolling on move.</summary>
    private int _lastRows = 8;

    /// <summary>Invoked with the chosen file on Enter; the picker hides itself first.</summary>
    public Action<FilePickerItem>? OnCommit { get; set; }

    /// <summary>Lazy preview fallback when the selected item carries no inline lines.</summary>
    public Func<FilePickerItem, IReadOnlyList<string>?>? PreviewProvider { get; set; }

    public bool Visible { get; private set; }

    public string Query => _query;

    /// <summary>Current filtered+ranked result set (all files when the query is empty).</summary>
    public IReadOnlyList<FilePickerItem> Results => _results;

    /// <summary>Index into <see cref="Results" />.</summary>
    public int SelectedIndex => _selected;

    /// <summary>
    ///     Currently selected entry, or <see cref="Maybe{T}.None" /> when the result set is
    ///     empty. An empty set is not a missing value, it is a state the picker is in
    ///     (#592).
    /// </summary>
    public Maybe<FilePickerItem> SelectedItem =>
        _results.Count == 0
            ? Maybe<FilePickerItem>.None
            : Maybe.From(_results[Math.Min(_selected, _results.Count - 1)]);

    /// <summary>Preview lines for the selected entry (provider fallback, clipped to <see cref="MaxPreviewLines" />).</summary>
    public IReadOnlyList<string> SelectedPreview => ResolvePreview(SelectedItem);

    public void Show(IReadOnlyList<FilePickerItem> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _query = string.Empty;
        _selected = 0;
        _offset = 0;
        Visible = true;
        Refilter();
    }

    public void Hide()
    {
        Visible = false;
        _results = [];
        _query = string.Empty;
        _selected = 0;
        _offset = 0;
    }

    /// <summary>
    /// Handles a key while visible. Returns true when consumed — hosts must
    /// stop routing the event (notably Enter/Escape) to other handlers.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Escape:
                Hide();
                return true;

            case KeyCode.Enter:
                if (_results.Count > 0)
                {
                    var chosen = _results[Math.Min(_selected, _results.Count - 1)];
                    Hide();
                    OnCommit?.Invoke(chosen);
                }

                return true;

            case KeyCode.Up when _results.Count > 0:
                Move(-1);
                return true;

            case KeyCode.Down when _results.Count > 0:
                Move(1);
                return true;

            case KeyCode.PageUp when _results.Count > 0:
                Move(-PageRows);
                return true;

            case KeyCode.PageDown when _results.Count > 0:
                Move(PageRows);
                return true;

            case KeyCode.Home when _results.Count > 0:
                MoveTo(0);
                return true;

            case KeyCode.End when _results.Count > 0:
                MoveTo(_results.Count - 1);
                return true;

            case KeyCode.Backspace:
                if (_query.Length > 0)
                {
                    _query = _query[..^1];
                    Refilter();
                }

                return true;

            case KeyCode.Char when key.Modifiers is KeyModifiers.None or KeyModifiers.Shift:
                _query += key.Character.ToString();
                Refilter();
                return true;

            default:
                return false; // not a picker key — let the host keep routing
        }
    }

    /// <summary>
    /// Paints the picker inside <paramref name="rect" /> (host-computed,
    /// typically a centered box): border, query prompt, file rows, preview
    /// pane when wide enough, and a hint footer. Pure over state — no layout
    /// side effects.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        if (!Visible || rect.Width < 8 || rect.Height < 4 || rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        PanelChrome.PaintBorderBox(buffer, rect);
        int innerW = rect.Width - 2;

        var queryStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        string queryText = "> " + _query;
        buffer.SetText(rect.X + 1, rect.Y + 1, queryText.AsSpan(0, Math.Min(queryText.Length, innerW)), queryStyle);

        int listTop = rect.Y + 2;
        int availableRows = rect.Height - 3 - 1; // query row + hint footer
        _lastRows = Math.Max(1, availableRows);
        EnsureVisible();

        // Wide box → list | preview split; narrow box → list only.
        bool split = innerW >= 40 && availableRows >= 2;
        int listW = innerW;
        int previewX = -1;
        int previewW = 0;
        if (split)
        {
            listW = Math.Max(15, (innerW * 55) / 100);
            previewX = rect.X + 1 + listW + 1;
            previewW = innerW - listW - 1;
        }

        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var titleStyle = ChatPalette.ToolArgs;
        var detailStyle = ChatPalette.Dim;
        int selectedRow = _results.Count > 0 ? Math.Min(_selected, _results.Count - 1) : -1;

        int painted = 0;
        for (int i = 0; i < _results.Count && painted < availableRows; i++)
        {
            if (i < _offset)
            {
                continue;
            }

            var item = _results[i];
            int y = listTop + painted;
            bool selected = i == selectedRow;
            string glyph = item.IsDirectory ? "▸ " : "· ";
            string text = glyph + item.Path;
            int titleLen = Math.Min(text.Length, listW);
            buffer.SetText(rect.X + 1, y, text.AsSpan(0, titleLen), selected ? selectedStyle : titleStyle);

            if (!string.IsNullOrEmpty(item.Detail) && titleLen < listW)
            {
                string suffix = "  " + item.Detail;
                int suffixLen = Math.Min(suffix.Length, listW - titleLen);
                buffer.SetText(rect.X + 1 + titleLen, y, suffix.AsSpan(0, suffixLen), detailStyle);
            }

            painted++;
        }

        if (_results.Count == 0 && availableRows > 0)
        {
            // Empty filter result: say so instead of a dead blank box.
            const string empty = "(no matches)";
            buffer.SetText(rect.X + 1, listTop, empty.AsSpan(0, Math.Min(empty.Length, listW)), ChatPalette.Dim);
        }
        else if (_results.Count - _offset > availableRows)
        {
            var more = $"… +{_results.Count - _offset - availableRows}";
            buffer.SetText(rect.X + 1, rect.Bottom - 2, more.AsSpan(0, Math.Min(more.Length, listW)), detailStyle);
        }

        if (split)
        {
            PaintPreview(buffer, previewX, listTop, previewW, availableRows);
        }

        const string hints = "↑↓ move · enter pick · esc close";
        if (innerW > hints.Length)
        {
            int hintX = (rect.X + 1 + innerW) - hints.Length;
            buffer.SetText(hintX, rect.Bottom - 2, hints, ChatPalette.Dim);
        }
    }

    private void Move(int delta)
    {
        _selected = Math.Clamp(_selected + delta, 0, _results.Count - 1);
        EnsureVisible();
    }

    private void MoveTo(int index)
    {
        _selected = Math.Clamp(index, 0, _results.Count - 1);
        EnsureVisible();
    }

    private void Refilter()
    {
        _results = FuzzyMatcher.Filter(_query, _files, static f => f.Path + " " + f.Detail);
        _selected = 0;
        _offset = 0;
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        if (_results.Count == 0)
        {
            return;
        }

        if (_selected < 0)
        {
            _selected = 0;
        }
        else if (_selected >= _results.Count)
        {
            _selected = _results.Count - 1;
        }

        if (_selected < _offset)
        {
            _offset = _selected;
        }
        else if (_selected >= _offset + _lastRows)
        {
            _offset = _selected - _lastRows + 1;
        }
    }

    private void PaintPreview(ScreenBuffer buffer, int x, int top, int width, int rows)
    {
        if (width < 8 || rows < 2)
        {
            return;
        }

        var selected = SelectedItem;
        string header = selected.HasNoValue ? "(no preview)" : TruncateMiddle(selected.Value.Path, width);
        buffer.SetText(x, top, header.AsSpan(0, Math.Min(header.Length, width)), ChatPalette.Dim);

        if (rows < 3)
        {
            return;
        }

        string rule = new string('─', Math.Min(width, 64));
        buffer.SetText(x, top + 1, rule.AsSpan(0, Math.Min(rule.Length, width)), new CellStyle(ChatPalette.Border));

        var lines = ResolvePreview(selected);
        if (lines.Count == 0)
        {
            const string empty = "(no preview)";
            if (rows > 2)
            {
                buffer.SetText(x, top + 2, empty.AsSpan(0, Math.Min(empty.Length, width)), ChatPalette.Dim);
            }

            return;
        }

        int bodyRows = rows - 2;
        for (int i = 0; i < lines.Count && i < bodyRows; i++)
        {
            string line = lines[i] ?? string.Empty;
            int len = Math.Min(line.Length, width);
            if (len > 0)
            {
                buffer.SetText(x, top + 2 + i, line.AsSpan(0, len), ChatPalette.ToolArgs);
            }
        }
    }

    private IReadOnlyList<string> ResolvePreview(Maybe<FilePickerItem> selected)
    {
        if (selected.HasNoValue)
        {
            return [];
        }

        FilePickerItem item = selected.Value;

        IReadOnlyList<string>? lines = item.PreviewLines ?? PreviewProvider?.Invoke(item);
        if (lines is null || lines.Count == 0)
        {
            return item.IsDirectory ? ["(directory)"] : [];
        }

        if (lines.Count <= MaxPreviewLines)
        {
            return lines;
        }

        var clipped = new List<string>(MaxPreviewLines);
        for (int i = 0; i < MaxPreviewLines; i++)
        {
            clipped.Add(lines[i] ?? string.Empty);
        }

        return clipped;
    }

    private static string TruncateMiddle(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
        {
            return path;
        }

        int slash = path.LastIndexOfAny(['/', '\\']);
        if (slash < 0 || path.Length - slash > max - 3)
        {
            return "…" + path[^(max - 1)..];
        }

        string file = path[slash..];
        string dir = path[..slash];

        // #482: same clamping rationale as PanelRows.ShortenPath — the directory budget
        // can reach 0, driving the from-end slice length to -1, and
        // Index.FromEnd(-1) throws ArgumentOutOfRangeException.
        int budget = max - file.Length - 3;
        if (dir.Length > budget)
        {
            int keep = Math.Max(0, budget - 1);
            dir = keep == 0 ? "…" : "…" + dir[^keep..];
        }

        return dir + file;
    }
}
