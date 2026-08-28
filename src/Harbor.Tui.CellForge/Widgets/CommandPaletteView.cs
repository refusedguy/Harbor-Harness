using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>One actionable entry of the command palette.</summary>
public sealed record CommandItem(string Id, string Title, string Detail = "", string Shortcut = "");

/// <summary>
/// Command palette overlay (ctrl+p pattern): fuzzy-filtered command list
/// with keyboard navigation and suggested defaults. The view is UI-only —
/// hosts subscribe via <see cref="OnCommit" /> and keep command semantics
/// out of the widget. Paint draws a bordered box; the host decides overlay
/// placement by passing a <see cref="Rect" />.
/// </summary>
public sealed class CommandPaletteView
{
    private const int PageRows = 5;

    private IReadOnlyList<CommandItem> _commands = [];
    private List<CommandItem> _results = [];
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

    /// <summary>Index into <see cref="Results" /> of the highlighted row.</summary>
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
        _selected = Math.Clamp(_selected + delta, 0, _results.Count - 1);
        EnsureVisible();
    }

    private void Refilter()
    {
        _results = FuzzyMatcher.Filter(_query, _commands, static c => c.Title + " " + c.Detail);
        _selected = 0;
        _offset = 0;
    }

    private void EnsureVisible()
    {
        if (_selected < _offset)
        {
            _offset = _selected;
        }
        else if (_selected >= _offset + _lastRows)
        {
            _offset = _selected - _lastRows + 1;
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
        int rows = Math.Min(_results.Count, rect.Height - 3 - 1); // query row + hint footer
        _lastRows = Math.Max(1, rows);
        EnsureVisible();

        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var titleStyle = ChatPalette.ToolArgs;
        var detailStyle = ChatPalette.Dim;
        for (int i = 0; i < rows; i++)
        {
            int resultIndex = _offset + i;
            var item = _results[resultIndex];
            bool selected = resultIndex == _selected;

            int y = listTop + i;
            buffer.SetText(rect.X + 1, y, item.Title.AsSpan(0, Math.Min(item.Title.Length, innerW)), selected ? selectedStyle : titleStyle);

            int x = rect.X + 1 + item.Title.Length + 1;
            int tailBudget = (rect.X + 1 + innerW) - x;
            if (item.Detail.Length > 0 && tailBudget > 0)
            {
                var detail = item.Detail.AsSpan(0, Math.Min(item.Detail.Length, tailBudget));
                buffer.SetText(x, y, detail, detailStyle);
                tailBudget -= detail.Length + 1;
            }

            if (item.Shortcut.Length > 0 && tailBudget >= item.Shortcut.Length)
            {
                int sx = (rect.X + 1 + innerW) - item.Shortcut.Length;
                buffer.SetText(sx, y, item.Shortcut.AsSpan(), detailStyle);
            }
        }

        if (_results.Count > rows)
        {
            var more = $"… +{_results.Count - rows}";
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
