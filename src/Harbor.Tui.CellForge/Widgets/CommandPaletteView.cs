using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>One actionable entry of the command palette.</summary>
public sealed record CommandItem(string Id, string Title, string Detail = "", string Shortcut = "", string Group = "");

/// <summary>
/// Command palette overlay (ctrl+p pattern): fuzzy-filtered command list
/// with keyboard navigation and suggested defaults. The view is UI-only —
/// hosts subscribe via <see cref="OnCommit" /> and keep command semantics
/// out of the widget. Paint draws a bordered box; the host decides overlay
/// placement by passing a <see cref="Rect" />.
/// </summary>
/// <remarks>
///     Items sharing a non-empty <see cref="CommandItem.Group" /> are
///     rendered under a non-selectable section header. Group headers do not
///     participate in keyboard selection — <see cref="SelectedIndex" /> and
///     <see cref="Move" /> skip them.
/// </remarks>
public sealed class CommandPaletteView
{
    private const int PageRows = 5;

    private IReadOnlyList<CommandItem> _commands = [];
    private List<CommandItem> _results = [];
    private List<(bool IsHeader, string Text)> _flatView = new();
    private List<int> _selectableIndices = new();
    private string _query = string.Empty;
    private int _selected;
    private int _offset;

    /// <summary>Rows the last Paint actually showed — drives list scrolling on move.</summary>
    private int _lastRows = 8;

    /// <summary>Invoked with the chosen item on Enter; the palette hides itself first.</summary>
    public Action<CommandItem>? OnCommit { get; set; }

    public bool Visible { get; private set; }

    public string Query => _query;

    /// <summary>Current filtered+ranked result set (all suggestions when the query is empty).</summary>
    public IReadOnlyList<CommandItem> Results => _results;

    /// <summary>Index into the selectable subset of <see cref="_flatView" />.</summary>
    public int SelectedIndex => _selected;

    public void Show(IReadOnlyList<CommandItem> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
        _query = string.Empty;
        _selected = 0;
        _offset = 0;
        Refilter();
        Visible = true;
    }

    public void Hide()
    {
        Visible = false;
        _results = [];
        _flatView = new();
        _selectableIndices = new();
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
                if (_selectableIndices.Count > 0)
                {
                    int resultIndex = Math.Min(_selected, _selectableIndices.Count - 1);
                    var chosen = _results[resultIndex];
                    Hide();
                    OnCommit?.Invoke(chosen);
                }

                return true;

            case KeyCode.Up when _selectableIndices.Count > 0:
                Move(-1);
                return true;

            case KeyCode.Down when _selectableIndices.Count > 0:
                Move(1);
                return true;

            case KeyCode.PageUp when _selectableIndices.Count > 0:
                Move(-PageRows);
                return true;

            case KeyCode.PageDown when _selectableIndices.Count > 0:
                Move(PageRows);
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
                return false; // not a palette key — let the host keep routing
        }
    }

    private void Move(int delta)
    {
        _selected = Math.Clamp(_selected + delta, 0, _selectableIndices.Count - 1);
        EnsureVisible();
    }

    private void Refilter()
    {
        _results = FuzzyMatcher.Filter(_query, _commands, static c => c.Title + " " + c.Detail);
        _flatView = new List<(bool, string)>(_results.Count + 8);
        _selectableIndices = new List<int>(_results.Count);

        string? lastGroup = null;
        foreach (var item in _results)
        {
            string? group = string.IsNullOrEmpty(item.Group) ? null : item.Group;
            if (group is not null && group != lastGroup)
            {
                _flatView.Add((true, group));
                lastGroup = group;
            }

            _selectableIndices.Add(_flatView.Count);
            _flatView.Add((false, item.Title));
        }

        _selected = 0;
        _offset = 0;
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        if (_selectableIndices.Count == 0)
        {
            return;
        }

        int targetVisual = _selectableIndices[Math.Min(_selected, _selectableIndices.Count - 1)];
        if (targetVisual < _offset)
        {
            _offset = targetVisual;
        }
        else if (targetVisual >= _offset + _lastRows)
        {
            _offset = targetVisual - _lastRows + 1;
        }
    }

    /// <summary>
    /// Paints the palette inside <paramref name="rect" /> (host-computed,
    /// typically a centered box): border, query prompt, the rows that fit,
    /// and a hint footer. Pure over state — no layout side effects.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        if (!Visible || rect.Width < 8 || rect.Height < 4 || rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        FillBox(buffer, rect);
        int innerW = rect.Width - 2;

        var queryStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var queryText = "> " + _query;
        buffer.SetText(rect.X + 1, rect.Y + 1, queryText.AsSpan(0, Math.Min(queryText.Length, innerW)), queryStyle);

        int listTop = rect.Y + 2;
        int availableRows = rect.Height - 3 - 1; // query row + hint footer
        _lastRows = Math.Max(1, availableRows);
        EnsureVisible();

        int selectedVisualIndex = _selectableIndices.Count > 0
            ? _selectableIndices[Math.Min(_selected, _selectableIndices.Count - 1)]
            : -1;

        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var titleStyle = ChatPalette.ToolArgs;
        var detailStyle = ChatPalette.Dim;
        var headerStyle = new CellStyle(ChatPalette.Muted, attrs: StyleAttr.Bold);
        int painted = 0;
        for (int i = 0; i < _flatView.Count && painted < availableRows; i++)
        {
            if (i < _offset)
            {
                continue;
            }

            var (isHeader, text) = _flatView[i];
            int y = listTop + painted;
            if (isHeader)
            {
                buffer.SetText(rect.X + 1, y, text.AsSpan(0, Math.Min(text.Length, innerW)), headerStyle);
            }
            else
            {
                bool selected = i == selectedVisualIndex;
                buffer.SetText(rect.X + 1, y, text.AsSpan(0, Math.Min(text.Length, innerW)), selected ? selectedStyle : titleStyle);
            }

            painted++;
        }

        int selectableCount = _selectableIndices.Count;
        if (selectableCount > availableRows)
        {
            var more = $"… +{selectableCount - availableRows}";
            buffer.SetText(rect.X + 1, rect.Bottom - 2, more.AsSpan(0, Math.Min(more.Length, innerW)), detailStyle);
        }

        const string hints = "↑↓ move · enter run · esc close";
        if (innerW > hints.Length)
        {
            int hintX = (rect.X + 1 + innerW) - hints.Length;
            buffer.SetText(hintX, rect.Bottom - 2, hints, ChatPalette.Dim);
        }
    }

    private static void FillBox(ScreenBuffer buffer, Rect rect)
    {
        var fillStyle = new CellStyle(ChatPalette.Panel);
        var borderStyle = new CellStyle(ChatPalette.Border);
        buffer.Fill(rect, Cell.From(new Rune(' '), fillStyle));

        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;
        buffer.At(x1, y1) = Cell.From(new Rune('╭'), borderStyle);
        buffer.At(x2, y1) = Cell.From(new Rune('╮'), borderStyle);
        buffer.At(x1, y2) = Cell.From(new Rune('╰'), borderStyle);
        buffer.At(x2, y2) = Cell.From(new Rune('╯'), borderStyle);
        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.At(x, y1) = Cell.From(new Rune('─'), borderStyle);
            buffer.At(x, y2) = Cell.From(new Rune('─'), borderStyle);
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.At(x1, y) = Cell.From(new Rune('│'), borderStyle);
            buffer.At(x2, y) = Cell.From(new Rune('│'), borderStyle);
        }
    }
}
