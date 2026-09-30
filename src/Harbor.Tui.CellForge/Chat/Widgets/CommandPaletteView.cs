using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Navigation;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>One actionable entry of the command palette.</summary>
public sealed record CommandItem(string Id, string Title, string Detail = "", string Shortcut = "", string Group = "");

/// <summary>Navigation frame for hierarchical drill-down palettes.
/// When <c>PreserveOrder</c> is true, the empty-query list keeps seed
/// order (tree hierarchies) instead of the default Group/Title sort.</summary>
public sealed record PaletteFrame(
    string Title,
    string Breadcrumb,
    IReadOnlyList<CommandItem> Items,
    Action<CommandItem>? OnCommit = null,
    bool IsInput = false,
    string InputPlaceholder = "",
    Action<string>? OnInputSubmit = null,
    Func<CommandItem, CancellationToken, Task>? OnCommitAsync = null,
    Func<string, CancellationToken, Task>? OnInputSubmitAsync = null,
    bool PreserveOrder = false);

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
///     <see cref="Move" /> skip them. List scrolling is owned by
///     <see cref="ScrollableViewport" /> with a <see cref="Scrollbar" />
///     overlay painted over the list rows' border column.
/// </remarks>
public sealed class CommandPaletteView
{
    private const int PageRows = 5;

    private readonly Stack<PaletteFrame> _frames = new();
    private IReadOnlyList<CommandItem> _commands = [];
    private List<CommandItem> _results = [];
    private List<(bool IsHeader, string Text, string Detail)> _flatView = new();
    private List<int> _selectableIndices = new();
    private string _query = string.Empty;
    private int _selected;

    /// <summary>
    /// List scroll state ([PRIM3c], part of #288): content rows are the flat
    /// view (headers + items), the viewport height is the last Paint's list
    /// height. Replaces the hand-rolled offset/rows pair.
    /// </summary>
    private readonly ScrollableViewport _viewport = new();

    /// <summary>Invoked with the chosen item on Enter; the palette hides itself first.</summary>
    public Action<CommandItem>? OnCommit { get; set; }

    public bool Visible { get; private set; }

    public string Query => _query;

    /// <summary>Current breadcrumb from the active frame (empty for root).</summary>
    public string CurrentBreadcrumb => _frames.Count > 0 ? _frames.Peek().Breadcrumb : string.Empty;

    /// <summary>Last submitted input value (consumed by the host after an input frame submit).</summary>
    public string LastInputValue { get; set; } = string.Empty;

    // Async continuations: the frame carries its own handler, so hosts need
    // no parallel stacks. Esc/Hide drops the pending continuation with the frame.
    private (CommandItem Item, Func<CommandItem, CancellationToken, Task> Handler)? _pendingCommit;
    private (string Value, Func<string, CancellationToken, Task> Handler)? _pendingInput;

    /// <summary>Take the staged commit continuation (cleared on read).</summary>
    public (CommandItem Item, Func<CommandItem, CancellationToken, Task> Handler)? TakePendingCommit()
    {
        var p = _pendingCommit;
        _pendingCommit = null;
        return p;
    }

    /// <summary>Take the staged input continuation (cleared on read).</summary>
    public (string Value, Func<string, CancellationToken, Task> Handler)? TakePendingInput()
    {
        var p = _pendingInput;
        _pendingInput = null;
        return p;
    }

    /// <summary>Raised after a drill-down frame is popped (Escape / Backspace on empty).</summary>
    public event EventHandler? FramePopped;

    /// <summary>Current filtered+ranked result set (all suggestions when the query is empty).</summary>
    public IReadOnlyList<CommandItem> Results => _results;

    /// <summary>Index into the selectable subset of <see cref="_flatView" />.</summary>
    public int SelectedIndex => _selected;

    /// <summary>Scroll viewport over the flat (headers + items) row space.</summary>
    public ScrollableViewport Viewport => _viewport;

    public void Show(IReadOnlyList<CommandItem> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _frames.Clear();
        PushFrame(new PaletteFrame("Commands", "", commands, null));
    }

    public void Hide()
    {
        _frames.Clear();
        _pendingCommit = null;
        _pendingInput = null;
        Visible = false;
        _results = [];
        _flatView = new();
        _selectableIndices = new();
        _query = string.Empty;
        _selected = 0;
        _viewport.SetTotal(0);
    }

    public void PushFrame(PaletteFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames.Push(frame);
        _pendingCommit = null;
        _pendingInput = null;
        _commands = frame.Items;
        _query = string.Empty;
        _selected = 0;
        LastInputValue = string.Empty;
        Visible = true;
        Refilter();
    }

    public bool PopFrame()
    {
        if (_frames.Count > 1)
        {
            _frames.Pop();
            _pendingCommit = null;
            _pendingInput = null;
            var prev = _frames.Peek();
            _commands = prev.Items;
            _query = string.Empty;
            _selected = 0;
            Refilter();
            FramePopped?.Invoke(this, EventArgs.Empty);
            return true;
        }

        Hide();
        return false;
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
                PopFrame();
                return true;

            case KeyCode.Enter:
                if (_frames.Count > 0 && _frames.Peek() is { IsInput: true } inputFrame)
                {
                    var val = _query.Trim();
                    LastInputValue = val;
                    if (inputFrame.OnInputSubmitAsync is { } submitAsync)
                    {
                        _pendingInput = (val, submitAsync);
                        return true;
                    }

                    var submit = inputFrame.OnInputSubmit;
                    Hide();
                    submit?.Invoke(val);
                    return true;
                }

                if (_selectableIndices.Count > 0)
                {
                    int resultIndex = Math.Min(_selected, _selectableIndices.Count - 1);
                    var chosen = _results[resultIndex];

                    if (_frames.Count > 0 && _frames.Peek().OnCommitAsync is { } commitAsync)
                    {
                        _pendingCommit = (chosen, commitAsync);
                        return true;
                    }

                    int frameDepthBefore = _frames.Count;

                    if (_frames.Count > 0 && _frames.Peek().OnCommit is { } frameCommit)
                    {
                        frameCommit.Invoke(chosen);
                    }
                    else if (OnCommit is not null)
                    {
                        OnCommit.Invoke(chosen);
                    }

                    if (_frames.Count <= frameDepthBefore)
                    {
                        Hide();
                    }
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
                else if (_frames.Count > 1)
                {
                    PopFrame();
                }

                return true;

            case KeyCode.Char when key.Modifiers.AcceptsTypedChar():
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

        if (_query.Length == 0 && !(_frames.TryPeek(out var top) && top.PreserveOrder))
        {
            _results.Sort((a, b) =>
            {
                int gc = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
                if (gc != 0) return gc;
                return string.Compare(a.Title, b.Title, StringComparison.Ordinal);
            });
        }

        _flatView = new List<(bool, string, string)>(_results.Count + 8);
        _selectableIndices = new List<int>(_results.Count);

        string? lastGroup = null;
        foreach (var item in _results)
        {
            string? group = string.IsNullOrEmpty(item.Group) ? null : item.Group;
            if (group is not null && group != lastGroup)
            {
                _flatView.Add((true, group, string.Empty));
                lastGroup = group;
            }

            _selectableIndices.Add(_flatView.Count);
            _flatView.Add((false, item.Title, item.Detail));
        }

        _selected = 0;
        _viewport.SetTotal(_flatView.Count);
        _viewport.SetOffset(0);
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        if (_selectableIndices.Count == 0 || _viewport.ViewportH <= 0)
        {
            return;
        }

        long targetVisual = _selectableIndices[Math.Min(_selected, _selectableIndices.Count - 1)];
        if (targetVisual < _viewport.Offset)
        {
            _viewport.SetOffset(targetVisual);
        }
        else if (targetVisual >= _viewport.Offset + _viewport.ViewportH)
        {
            _viewport.SetOffset(targetVisual - _viewport.ViewportH + 1);
        }
    }

    /// <summary>
    /// Paints the palette inside <paramref name="rect" /> (host-computed,
    /// typically a centered box): border, query prompt, the rows that fit,
    /// a scrollbar overlay when the list overflows, and a hint footer.
    /// Syncs <see cref="Viewport" /> to the list geometry first, so the
    /// selection is always in view.
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
        string queryText;
        if (_frames.Count > 0 && _frames.Peek().IsInput)
        {
            var frame = _frames.Peek();
            if (_query.Length > 0)
            {
                queryText = "> " + _query;
            }
            else if (!string.IsNullOrEmpty(frame.InputPlaceholder))
            {
                queryText = "> " + frame.InputPlaceholder;
                queryStyle = ChatPalette.Dim;
            }
            else
            {
                queryText = "> ";
            }
        }
        else if (!string.IsNullOrEmpty(CurrentBreadcrumb))
        {
            queryText = "> [" + CurrentBreadcrumb + "] " + _query;
        }
        else
        {
            queryText = "> " + _query;
        }

        buffer.SetText(rect.X + 1, rect.Y + 1, queryText.AsSpan(0, Math.Min(queryText.Length, innerW)), queryStyle);

        int listTop = rect.Y + 2;
        int availableRows = rect.Height - 3 - 1; // query row + hint footer
        _viewport.Configure(_flatView.Count, Math.Max(1, availableRows));
        EnsureVisible();

        int selectedVisualIndex = _selectableIndices.Count > 0
            ? _selectableIndices[Math.Min(_selected, _selectableIndices.Count - 1)]
            : -1;

        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var titleStyle = ChatPalette.ToolArgs;
        var detailStyle = ChatPalette.Dim;
        var headerStyle = new CellStyle(ChatPalette.Muted, attrs: StyleAttr.Bold);
        int painted = 0;
        int firstRow = (int)_viewport.Offset;
        for (int i = firstRow; i < _flatView.Count && painted < availableRows; i++)
        {
            var (isHeader, text, detail) = _flatView[i];
            int y = listTop + painted;
            if (isHeader)
            {
                buffer.SetText(rect.X + 1, y, text.AsSpan(0, Math.Min(text.Length, innerW)), headerStyle);
            }
            else
            {
                bool selected = i == selectedVisualIndex;
                int titleLen = Math.Min(text.Length, innerW);
                buffer.SetText(rect.X + 1, y, text.AsSpan(0, titleLen), selected ? selectedStyle : titleStyle);

                // Second plan: the item detail (short id, status, …) dimmed
                // after the title when space remains. Empty details paint
                // exactly as before.
                if (!string.IsNullOrEmpty(detail) && titleLen < innerW)
                {
                    string suffix = "  " + detail;
                    int suffixLen = Math.Min(suffix.Length, innerW - titleLen);
                    buffer.SetText(rect.X + 1 + titleLen, y, suffix.AsSpan(0, suffixLen), detailStyle);
                }
            }

            painted++;
        }

        if (availableRows > 0)
        {
            // Scrollbar overlay on the border column of the list rows only —
            // hidden (buffer untouched) when the content fits, so small
            // palettes paint byte-identical to before.
            _ = Scrollbar.TryPaint(buffer, new Rect(rect.Right - 1, listTop, 1, availableRows), _viewport);
        }

        int selectableCount = _selectableIndices.Count;
        if (_flatView.Count == 0 && availableRows > 0)
        {
            // Empty filter result: say so instead of a dead blank box.
            const string empty = "(no matches)";
            buffer.SetText(rect.X + 1, listTop, empty.AsSpan(0, Math.Min(empty.Length, innerW)), ChatPalette.Dim);
        }
        else if (selectableCount > availableRows)
        {
            var more = $"… +{selectableCount - availableRows}";
            buffer.SetText(rect.X + 1, rect.Bottom - 2, more.AsSpan(0, Math.Min(more.Length, innerW)), detailStyle);
        }

        string hints = _frames.Count > 0 && _frames.Peek().IsInput
            ? "enter submit · esc back"
            : _frames.Count > 1
                ? "↑↓ move · enter run · esc back · ⌫ back"
                : "↑↓ move · enter run · esc close";
        if (innerW > hints.Length)
        {
            int hintX = (rect.X + 1 + innerW) - hints.Length;
            buffer.SetText(hintX, rect.Bottom - 2, hints, ChatPalette.Dim);
        }
    }

    /// <summary>
    /// Shows the CF-E-017 default catalog (slash + builtin) without the host
    /// assembling item lists by hand. Behavior of <see cref="Show" />,
    /// filtering, groups, navigation and <see cref="OnCommit" /> is unchanged.
    /// </summary>
    /// <param name="useNerdFont">When true, builtin titles use Nerd Font glyphs; otherwise ASCII fallbacks.</param>
    public void ShowDefaultCatalog(bool useNerdFont = false)
    {
        Show(CommandPaletteCatalog.GetDefaultCatalog(useNerdFont));
    }
}
