using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
// The global KeyEvent/KeyCode/KeyModifiers aliases point at the legacy Rendering
// input vocabulary, while KeyContext/KeyGesture live in the engine vocabulary.
// Name the engine namespace so the two never get mixed up at a call site (#3628).
using EngineInput = Harbor.Tui.CellForge.Input;
using KeyContext = Harbor.Tui.CellForge.Input.KeyContext;
using KeyGesture = Harbor.Tui.CellForge.Input.KeyGesture;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.ViewModels;

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
///
///     Selection + scroll live outside the widget (#1158, ratatui
///     <c>StatefulWidget</c> analog, same recipe as <c>FilePickerView</c>
///     #1391): <see cref="State"/> survives frames and owns the selectable
///     axis, while this type owns content (frames, query, filter) and
///     geometry. <see cref="Paint(ScreenBuffer, Rect, GenericNavState)"/>
///     renders with any count-compatible state, so two palettes can share
///     one. The legacy selection API + <see cref="Paint(ScreenBuffer,
///     Rect)"/> path is unchanged.
/// </remarks>
public sealed class CommandPaletteView : IStatefulGenericWidget<GenericNavState>
{
    private const int PageRows = 5;

    // #3628: the palette is a MODAL key context. Gestures resolve to opaque
    // action ids owned here, never to ChatKeyMap's global vocabulary. PageUp
    // and PageDown share one action; direction comes from the key. Arbitrary
    // runes are the one gesture a KeyGesture cannot express (it matches one
    // exact rune), so they resolve to TypeAction by fallback in HandleKey.
    private const string PageAction = "page";
    private const string TypeAction = "type";

    private static readonly EngineInput.KeyContext PaletteContext = CreatePaletteContext();

    private static EngineInput.KeyContext CreatePaletteContext()
    {
        var context = new EngineInput.KeyContext("palette");
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Escape), "back");
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Enter), "commit");
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Up), "move-up");
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Down), "move-down");
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.PageUp), PageAction);
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.PageDown), PageAction);
        context.Bind(EngineInput.KeyGesture.ForKey(EngineInput.KeyCode.Backspace), "backspace");
        return context;
    }

    // The widget's public key type is the legacy Rendering KeyEvent; KeyContext
    // matches the engine's verbatim-identical port. Map by ordinal so the two
    // vocabularies stay decoupled (no cast between unrelated types at call sites).
    private static EngineInput.KeyEvent ToEngineKey(in KeyEvent key) => new(
        (EngineInput.KeyCode)(int)key.Key,
        key.Character,
        (EngineInput.KeyModifiers)(int)key.Modifiers,
        (EngineInput.KeyEventType)(int)key.EventType,
        key.IsKittyEncoded,
        key.Codepoint);

    // Local translation of the opaque action ids to a closed set. The gate on
    // string-dispatched switch STATEMENTS (#1575) keeps those ladders out of
    // src/; an enum dispatch keeps this one out while the KeyContext stays the
    // single owner of the gesture -> action vocabulary.
    private enum PaletteAction : byte
    {
        None,
        Back,
        Commit,
        MoveUp,
        MoveDown,
        Page,
        Backspace,
        Type,
    }

    private static PaletteAction ResolveAction(string? action) => action switch
    {
        "back" => PaletteAction.Back,
        "commit" => PaletteAction.Commit,
        "move-up" => PaletteAction.MoveUp,
        "move-down" => PaletteAction.MoveDown,
        PageAction => PaletteAction.Page,
        "backspace" => PaletteAction.Backspace,
        TypeAction => PaletteAction.Type,
        _ => PaletteAction.None,
    };

    private readonly Stack<PaletteFrame> _frames = new();

    // #3629: the same BCL-only policies the desktop palette uses. They live
    // across Show/Hide so usage ranking and query history survive reopening.
    private readonly CommandPalettePolicies _policies = new();

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
    private readonly GenericNavState _navState = new();

    /// <summary>
    /// External navigation state (selection + scroll over the selectable
    /// subset), count-synced with the selectable count on every <see
    /// cref="Refilter"/>/<see cref="Hide"/>. Mutating it directly (e.g. <see
    /// cref="GenericNavState.SelectNext"/>) drives the <see
    /// cref="Paint(ScreenBuffer, Rect, GenericNavState)"/> overload.
    /// </summary>
    public GenericNavState State => _navState;

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

    /// <summary>Queries committed through this palette, oldest first (#2682 Z01).</summary>
    public IReadOnlyList<string> RecentQueries => _policies.RecentQueries;

    /// <summary>Exact-query aliases, case-insensitive (#2682 Z03). Alias values are canonical query text, not item ids.</summary>
    public IReadOnlyDictionary<string, string> Aliases => _policies.Aliases;

    /// <summary>Map an alias such as "quit" to the query text of its canonical row (#2682 Z03).</summary>
    public void AddAlias(string alias, string canonicalQuery) => _policies.Aliases[alias] = canonicalQuery;

    /// <summary>Remove an alias. Returns false when it was not present.</summary>
    public bool RemoveAlias(string alias) => _policies.Aliases.Remove(alias);

    /// <summary>Clear all aliases, e.g. when a host swaps in a different command catalog.</summary>
    public void ClearAliases() => _policies.Aliases.Clear();

    /// <summary>Index into the selectable subset of <see cref="_flatView" />.</summary>
    public int SelectedIndex => _selected;

    /// <summary>Scroll viewport over the flat (headers + items) row space.</summary>
    public ScrollableViewport Viewport => _viewport;

    public void Show(IReadOnlyList<CommandItem> commands)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(commands);
        _frames.Clear();
        PushFrame(new PaletteFrame("Commands", "", commands, null));
    }

    public void Hide()
    {
        _frames.Clear();
        _policies.ResetHistoryNavigation();
        _pendingCommit = null;
        _pendingInput = null;
        Visible = false;
        _results = [];
        _flatView = new();
        _selectableIndices = new();
        _query = string.Empty;
        _selected = 0;
        _viewport.SetTotal(0);
        _navState.SetContent(0);
    }

    public void PushFrame(PaletteFrame frame)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(frame);
        _frames.Push(frame);
        _pendingCommit = null;
        _pendingInput = null;
        _commands = frame.Items;
        _query = string.Empty;
        _policies.ResetHistoryNavigation();
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
            _policies.ResetHistoryNavigation();
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

        string? action = PaletteContext.TryMatch(ToEngineKey(key), out EngineInput.ContextBinding hit)
            ? hit.ActionId
            : key.Key == KeyCode.Char && key.Modifiers.AcceptsTypedChar() ? TypeAction : null;

        switch (ResolveAction(action))
        {
            case PaletteAction.Back:
                PopFrame();
                return true;

            case PaletteAction.Commit:
                Commit();
                return true;

            case PaletteAction.MoveUp when _selectableIndices.Count > 0 || _query.Length > 0:
                if (TryNavigateHistory(up: true))
                {
                    return true;
                }

                if (_selectableIndices.Count > 0)
                {
                    Move(-1);
                }

                return true;

            case PaletteAction.MoveDown when _selectableIndices.Count > 0 || _query.Length > 0:
                if (TryNavigateHistory(up: false))
                {
                    return true;
                }

                if (_selectableIndices.Count > 0)
                {
                    Move(1);
                }

                return true;

            case PaletteAction.Page when _selectableIndices.Count > 0:
                Move(key.Key == KeyCode.PageUp ? -PageRows : PageRows);
                return true;

            case PaletteAction.Backspace:
                if (_query.Length > 0)
                {
                    _policies.ResetHistoryNavigation();
                    _query = _query[..^1];
                    Refilter();
                }
                else if (_frames.Count > 1)
                {
                    PopFrame();
                }

                return true;

            case PaletteAction.Type:
                _policies.ResetHistoryNavigation();
                _query += key.Character.ToString();
                Refilter();
                return true;

            case PaletteAction.None:
            default:
                return false; // not a palette key — let the host keep routing
        }
    }

    /// <summary>Enter: stage async continuations on the frame, never on a host-side stack.</summary>
    private void Commit()
    {
        if (_frames.Count > 0 && _frames.Peek() is { IsInput: true } inputFrame)
        {
            var val = _query.Trim();
            LastInputValue = val;
            if (inputFrame.OnInputSubmitAsync is { } submitAsync)
            {
                _pendingInput = (val, submitAsync);
                return;
            }

            var submit = inputFrame.OnInputSubmit;
            Hide();
            submit?.Invoke(val);
            return;
        }

        if (_selectableIndices.Count == 0)
        {
            return;
        }

        InvokeSelected();
    }

    private void Move(int delta)
    {
        _selected = Math.Clamp(_selected + delta, 0, _selectableIndices.Count - 1);
        _navState.MoveTo(_selected); // #3628: keyboard nav drives external state too
        EnsureVisible();
    }

    /// <summary>
    /// #2682 Z01: Up/Down first walk typed query history. The typed text is
    /// adopted as the new query and refiltered; the result selection stays put.
    /// </summary>
    private bool TryNavigateHistory(bool up)
    {
        if (!_policies.TryNavigateHistory(up, _query, out string next))
        {
            return false;
        }

        _query = next;
        Refilter();
        return true;
    }

    /// <summary>
    /// Commit the highlighted row. The shared policies record the typed query
    /// and the row's usage only when a command actually ran, not when the
    /// frame pushed a drill-down or the host handler threw (#2682 Z01/Z02).
    /// </summary>
    private void InvokeSelected()
    {
        int resultIndex = Math.Min(_selected, _selectableIndices.Count - 1);
        var chosen = _results[resultIndex];

        if (_frames.Count > 0 && _frames.Peek().OnCommitAsync is { } commitAsync)
        {
            _pendingCommit = (chosen, commitAsync);
            NoteSelection(chosen);
            return;
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
            NoteSelection(chosen);
            Hide();
        }
    }

    /// <summary>#2682 Z01/Z02: commit the typed query and the chosen row's usage count.</summary>
    private void NoteSelection(CommandItem item)
    {
        _policies.NoteQuery(_query);
        _policies.NoteInvocation(item.Group, item.Title);
    }

    /// <summary>Adapts the engine-local scorer to the shared policy's (text, query) shape.</summary>
    private static int Score(string text, string query) => FuzzyMatcher.Score(query, text) ?? -1;

    private void Refilter()
    {
        // Filter returns a thread-local reusable buffer; copy it before the
        // group/title sort below so _commands (the frame's source) is never
        // reordered and this palette's results survive another palette's pass.
        // #3629: aliases resolve before the fuzzy pass, exactly as in the
        // desktop palette; FuzzyMatcher remains this engine's scorer.
        string query = _policies.ResolveQuery(_query);
        List<CommandItem> matches = new(FuzzyMatcher.Filter(query, _commands, static c => c.Title + " " + c.Detail));

        if (query.Length == 0 && !(_frames.TryPeek(out var top) && top.PreserveOrder))
        {
            matches.Sort((a, b) =>
            {
                int gc = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
                if (gc != 0) return gc;
                return string.Compare(a.Title, b.Title, StringComparison.Ordinal);
            });
        }

        // #2682 Z02/Z03: usage leads within a group, the weighted score breaks
        // ties, and the original position keeps equal rows stable. Grouping
        // stays contiguous so the header pass below needs no second pass.
        var buckets = new Dictionary<string, List<(CommandItem Item, double Score, int Index)>>(StringComparer.Ordinal);
        var groupOrder = new List<string>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            CommandItem item = matches[i];
            string group = item.Group;
            if (!buckets.TryGetValue(group, out var bucket))
            {
                bucket = new List<(CommandItem, double, int)>();
                buckets.Add(group, bucket);
                groupOrder.Add(group);
            }

            double score = query.Length == 0
                ? 0d
                : CommandPalettePolicies.WeightedScore(Score, item.Title, item.Detail, item.Group, query);
            bucket.Add((item, score, i));
        }

        _results = new List<CommandItem>(matches.Count);
        foreach (string group in groupOrder)
        {
            foreach (var entry in buckets[group]
                .OrderByDescending(e => _policies.InvocationCount(e.Item.Group, e.Item.Title))
                .ThenByDescending(e => e.Score)
                .ThenBy(e => e.Index))
            {
                _results.Add(entry.Item);
            }
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
        _navState.SetContent(_selectableIndices.Count);
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
    /// <remarks>Legacy path: highlights the widget-local selection, scrolled by the widget-local viewport.</remarks>
    public void Paint(ScreenBuffer buffer, Rect rect) => PaintCore(buffer, rect, null);

    /// <summary>
    /// Paints with an external navigation state (#1158): when <paramref
    /// name="navState"/> is count-compatible (<c>Count == selectable count</c>)
    /// the highlight and scroll come from it, so one state object can drive
    /// two palettes; otherwise falls back to the widget-local selection. The
    /// frame/query/filter axis stays widget-local. Group headers never take
    /// the selection — the state counts selectable entries only.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect, GenericNavState navState)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(navState);
        PaintCore(buffer, rect, navState);
    }

    private void PaintCore(ScreenBuffer buffer, Rect rect, GenericNavState? navState)
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
        int selIdx;
        int pickedVis;
        int firstVis;
        bool useExternal;
        if (navState is not null && _selectableIndices.Count > 0 && navState.Count == _selectableIndices.Count)
        {
            navState.Sync(Math.Max(1, availableRows));
            selIdx = navState.Selected;
            pickedVis = selIdx >= 0 && selIdx < _selectableIndices.Count
                ? _selectableIndices[selIdx]
                : -1;
            firstVis = _selectableIndices[Math.Clamp(navState.Offset, 0, _selectableIndices.Count - 1)];
            if (firstVis > 0 && _flatView[firstVis - 1].IsHeader)
            {
                firstVis--;
            }

            useExternal = true;
        }
        else
        {
            _viewport.Configure(_flatView.Count, Math.Max(1, availableRows));
            EnsureVisible();
            selIdx = _selectableIndices.Count > 0 ? Math.Min(_selected, _selectableIndices.Count - 1) : -1;
            pickedVis = selIdx >= 0 ? _selectableIndices[selIdx] : -1;
            firstVis = (int)_viewport.Offset;
            useExternal = false;
        }

        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var titleStyle = ChatPalette.ToolArgs;
        var detailStyle = ChatPalette.Dim;
        var headerStyle = new CellStyle(ChatPalette.Muted, attrs: StyleAttr.Bold);
        // Item priority (ordinary title → picked) lives in StylePriority.
        var itemCatalog = new StyleCatalog(titleStyle, Selected: selectedStyle);
        int painted = 0;
        for (int i = firstVis; i < _flatView.Count && painted < availableRows; i++)
        {
            var (isHeader, text, detail) = _flatView[i];
            int y = listTop + painted;
            if (isHeader)
            {
                buffer.SetText(rect.X + 1, y, text.AsSpan(0, Math.Min(text.Length, innerW)), headerStyle);
            }
            else
            {
                bool isPicked = i == pickedVis;
                int titleLen = Math.Min(text.Length, innerW);
                buffer.SetText(rect.X + 1, y, text.AsSpan(0, titleLen),
                    StylePriority.Resolve(itemCatalog, isPicked ? VisualState.Selected : VisualState.None, disabled: false));

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
            // palettes paint byte-identical to before. The stateful path
            // reports the same visual triple without touching Viewport, so a
            // stateful paint leaves the legacy scroll alone.
            if (useExternal)
            {
                _ = Scrollbar.TryPaint(
                    buffer, new Rect(rect.Right - 1, listTop, 1, availableRows),
                    _flatView.Count, firstVis, availableRows);
            }
            else
            {
                _ = Scrollbar.TryPaint(buffer, new Rect(rect.Right - 1, listTop, 1, availableRows), _viewport);
            }
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

    /// <summary>
    /// Maps one provider hit to a view item (hit group → section header).
    /// </summary>
    public static CommandItem FromHit(CommandHit hit)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(hit);
        return new CommandItem(hit.Id, hit.Title, hit.Detail, string.Empty, hit.Group);
    }

    /// <summary>
    /// Shows merged provider hits (#1186 T4). Hits arrive pre-ranked from
    /// <see cref="CommandProviderRegistry" /> — discovery hits in provider
    /// order, scored hits best-first — so the frame keeps seed order
    /// (<c>PreserveOrder</c>) instead of the default Group/Title sort.
    /// Typing still fuzzy-filters via <see cref="FuzzyMatcher" /> like any
    /// frame; groups still render as section headers.
    /// </summary>
    public void ShowFromHits(IReadOnlyList<CommandHit> hits)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(hits);
        var items = new List<CommandItem>(hits.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            items.Add(FromHit(hits[i]));
        }

        PushFrame(new PaletteFrame("Commands", "", items, null, PreserveOrder: true));
    }
}
