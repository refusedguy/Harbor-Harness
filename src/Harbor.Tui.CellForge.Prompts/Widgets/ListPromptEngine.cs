using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// How a <see cref="ListPromptState{T}"/> treats group headers during
/// navigation (SC7, part of #1215, epic #1195): port of the Spectre.Console
/// <c>Prompts/List</c> selection-mode axis, minus the console/render half
/// (<c>ListPrompt</c>/<c>RenderHook</c> are not portable — painting stays in
/// the owning widget: <see cref="DialogSelectList"/>,
/// <see cref="CommandPaletteView"/>, <see cref="FilePickerView"/>).
/// </summary>
public enum ListSelectionMode
{
    /// <summary>Every flat row (headers included) is navigable and committable.</summary>
    Single = 0,

    /// <summary>Group headers are skipped — navigation runs on a precomputed
    /// leaf-index table (the palette section-header idiom).</summary>
    Leaf = 1,

    /// <summary>Headers are navigable stops (unlike <see cref="Leaf"/>),
    /// plus toggle with parent recount: toggling a group flips its whole
    /// subtree, and an ancestor is selected exactly when all of its
    /// children are.</summary>
    Multiple = 2,
}

/// <summary>
/// What a <see cref="IListPromptStrategy{T}"/> decided for one key
/// (Spectre.Console <c>ListPromptInputResult</c> verbatim, minus console coupling).
/// </summary>
public enum ListPromptInputResult
{
    /// <summary>Key ignored — keep routing it to other handlers.</summary>
    None = 0,

    /// <summary>State changed — repaint.</summary>
    Refresh = 1,

    /// <summary>Commit gesture — the host reads the selection and hides.</summary>
    Submit = 2,

    /// <summary>Cancel gesture — the host hides without committing.</summary>
    Abort = 3,
}

/// <summary>
/// One node of a prompt list (Spectre.Console <c>ListPromptItem{T}</c>
/// verbatim, minus the multi-selection interfaces): a leaf value, or a group
/// when <see cref="Children"/> is non-empty. Groups render as non-selectable
/// section headers in <see cref="ListSelectionMode.Leaf"/>/
/// <see cref="ListSelectionMode.Multiple"/>.
/// </summary>
/// <typeparam name="T">Leaf payload type.</typeparam>
public sealed class ListPromptItem<T> where T : notnull
{
    /// <summary>Leaf payload (group caption for group nodes).</summary>
    public T Data { get; }

    /// <summary>Parent group (null for roots).</summary>
    public ListPromptItem<T>? Parent { get; }

    /// <summary>Child nodes (empty for leaves).</summary>
    public List<ListPromptItem<T>> Children { get; } = [];

    /// <summary>Nesting depth (0 for roots).</summary>
    public int Depth { get; }

    /// <summary>Multi-mode checkmark. Recounted bottom-up by
    /// <see cref="ListPromptState{T}.ToggleCurrent"/> — hosts should not set it by hand.</summary>
    public bool IsSelected { get; set; }

    /// <summary>True when this node has children (renders as a header, never committed).</summary>
    public bool IsGroup => Children.Count > 0;

    public ListPromptItem(T data, ListPromptItem<T>? parent = null)
    {
        Ensure.NotNull(data);
        Data = data;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    /// <summary>Appends a child leaf/group and returns it.</summary>
    public ListPromptItem<T> AddChild(T item)
    {
        var node = new ListPromptItem<T>(item, this);
        Children.Add(node);
        return node;
    }

    /// <summary>Depth-first pre-order walk (stack-allocated walk, no recursion).</summary>
    public IEnumerable<ListPromptItem<T>> Traverse(bool includeSelf)
    {
        var stack = new Stack<ListPromptItem<T>>();
        if (includeSelf)
        {
            stack.Push(this);
        }
        else
        {
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                stack.Push(Children[i]);
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (int i = current.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(current.Children[i]);
            }
        }
    }
}

/// <summary>
/// Forest of <see cref="ListPromptItem{T}"/> roots (Spectre.Console
/// <c>ListPromptTree{T}</c> minus the ctor comparer — lookup takes an
/// optional comparer per call). Flatten with <see cref="Flatten"/> into the
/// row order a <see cref="ListPromptState{T}"/> consumes.
/// </summary>
/// <typeparam name="T">Leaf payload type.</typeparam>
public sealed class ListPromptTree<T> where T : notnull
{
    private readonly List<ListPromptItem<T>> _roots = [];

    /// <summary>Adds a root node (groups are built via <see cref="ListPromptItem{T}.AddChild"/>).</summary>
    public void Add(ListPromptItem<T> node)
    {
        Ensure.NotNull(node);
        _roots.Add(node);
    }

    /// <summary>Depth-first pre-order walk over all roots.</summary>
    public IEnumerable<ListPromptItem<T>> Traverse()
    {
        for (int r = 0; r < _roots.Count; r++)
        {
            foreach (var node in _roots[r].Traverse(includeSelf: true))
            {
                yield return node;
            }
        }
    }

    /// <summary>Flat row order (headers interleaved) for <see cref="ListPromptState{T}"/>.</summary>
    public List<ListPromptItem<T>> Flatten()
    {
        var flat = new List<ListPromptItem<T>>();
        foreach (var node in Traverse())
        {
            flat.Add(node);
        }

        return flat;
    }

    /// <summary>First node whose <c>Data</c> equals <paramref name="value"/> (DFS), or null.</summary>
    public ListPromptItem<T>? FindValue(T value, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        foreach (var node in Traverse())
        {
            if (comparer.Equals(value, node.Data))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Flat index of <paramref name="value"/>, or null when absent.</summary>
    public int? IndexOfValue(T value, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        int index = 0;
        foreach (var node in Traverse())
        {
            if (comparer.Equals(value, node.Data))
            {
                return index;
            }

            index++;
        }

        return null;
    }
}

/// <summary>
/// Which mode of a list prompt a command is live in: the scope gate that
/// replaced the two <c>when</c>-clauses the old <c>switch</c> carried on its
/// two <see cref="KeyCode.Char"/> arms (<c>SearchEnabled</c> and
/// <c>!SearchEnabled</c>). A command in <see cref="Any"/> is live in both
/// modes — the arrows, Home/End and the page keys.
/// </summary>
internal enum ListPromptScope
{
    /// <summary>Live in every mode.</summary>
    Any = 0,

    /// <summary>Live only while the incremental search is on.</summary>
    Search = 1,

    /// <summary>Live only while the incremental search is off (the vim keys).</summary>
    Navigation = 2,
}

/// <summary>
/// The gesture a list-prompt command fires on: the key code, an optional
/// exact rune, and whether the press must be typeable text.
/// </summary>
/// <remarks>
/// Modifiers are tested exactly where the old <c>switch</c> tested them: a
/// char gesture refuses a command modifier (Ctrl/Alt/Meta — that chord
/// belongs to the host) through <see cref="KeyModifierGate.AcceptsTypedChar"/>,
/// while the named keys do not look at modifiers at all, so a chorded arrow
/// still navigates, exactly as before.
/// </remarks>
/// <param name="Key">The key code that fires the command.</param>
/// <param name="Character">The exact rune that fires it (null = any rune).</param>
/// <param name="TypedOnly">Whether the press must be typeable text.</param>
internal readonly record struct ListPromptGesture(KeyCode Key, Rune? Character = null, bool TypedOnly = false)
{
    /// <summary>Whether the gesture fires on <paramref name="key"/>.</summary>
    public bool Matches(in KeyEvent key)
    {
        if (key.Key != Key)
        {
            return false;
        }

        if (Character is { } rune && key.Character != rune)
        {
            return false;
        }

        return !TypedOnly || key.Modifiers.AcceptsTypedChar();
    }
}

/// <summary>
/// The state surface a list-prompt command drives. Deliberately non-generic:
/// a command moves the cursor and edits the search text, never the leaf
/// payload, so the command vocabulary stays one closed set instead of one set
/// per payload type.
/// </summary>
internal interface IListPromptTarget
{
    /// <summary>Whether the incremental search is on.</summary>
    bool SearchEnabled { get; }

    /// <summary>The current incremental-search text.</summary>
    string SearchText { get; }

    /// <summary>Steps the cursor by a signed delta in the active (leaf or flat) space.</summary>
    bool MoveBy(int delta);

    /// <summary>Parks the cursor on the first/last navigable row.</summary>
    bool MoveToEdge(bool toStart);

    /// <summary>Jumps by whole pages.</summary>
    bool PageBy(int pages);

    /// <summary>Applies a search string: jumps to the first match and stores the text.</summary>
    bool ApplySearch(string search);

    /// <summary>Cycles to the next search match.</summary>
    bool CycleSearchMatch();
}

/// <summary>
/// One action of a list prompt, as an object (GoF Command — the repo's §4
/// convention: a command is an object, not a <c>switch</c> arm). The command
/// owns its own precondition (<see cref="CanExecute"/>, the old
/// <c>when</c>-clause) and its action (<see cref="Execute"/>), so
/// <see cref="ListPromptState{T}.Update"/> is a table lookup plus an execute
/// and a new key is one row.
/// </summary>
internal interface IListPromptCommand
{
    /// <summary>Whether the command may run for this key right now.</summary>
    bool CanExecute(IListPromptTarget target, in KeyEvent key);

    /// <summary>Runs the command; true when the state changed.</summary>
    bool Execute(IListPromptTarget target, in KeyEvent key);
}

/// <summary>Moves the cursor by a signed delta (Up/Down, and the vim j/J/k/K).</summary>
/// <param name="Delta">The signed step.</param>
internal sealed record ListPromptMoveByCommand(int Delta) : IListPromptCommand
{
    /// <inheritdoc/>
    public bool CanExecute(IListPromptTarget target, in KeyEvent key) => true;

    /// <inheritdoc/>
    public bool Execute(IListPromptTarget target, in KeyEvent key) => target.MoveBy(Delta);
}

/// <summary>Parks the cursor on the first/last navigable row (Home/End, and the vim g/G).</summary>
/// <param name="ToStart">True for the first row, false for the last.</param>
internal sealed record ListPromptMoveToEdgeCommand(bool ToStart) : IListPromptCommand
{
    /// <inheritdoc/>
    public bool CanExecute(IListPromptTarget target, in KeyEvent key) => true;

    /// <inheritdoc/>
    public bool Execute(IListPromptTarget target, in KeyEvent key) => target.MoveToEdge(ToStart);
}

/// <summary>Jumps by whole pages (PageUp/PageDown).</summary>
/// <param name="Pages">The signed page count.</param>
internal sealed record ListPromptPageByCommand(int Pages) : IListPromptCommand
{
    /// <inheritdoc/>
    public bool CanExecute(IListPromptTarget target, in KeyEvent key) => true;

    /// <inheritdoc/>
    public bool Execute(IListPromptTarget target, in KeyEvent key) => target.PageBy(Pages);
}

/// <summary>Shrinks the incremental search by one character (Backspace).</summary>
internal sealed record ListPromptSearchBackspaceCommand : IListPromptCommand
{
    /// <inheritdoc/>
    public bool CanExecute(IListPromptTarget target, in KeyEvent key) => target.SearchText.Length > 0;

    /// <inheritdoc/>
    public bool Execute(IListPromptTarget target, in KeyEvent key) => target.ApplySearch(target.SearchText[..^1]);
}

/// <summary>Cycles to the next search match (Tab).</summary>
internal sealed record ListPromptSearchCycleCommand : IListPromptCommand
{
    /// <inheritdoc/>
    public bool CanExecute(IListPromptTarget target, in KeyEvent key) => target.SearchText.Length > 0;

    /// <inheritdoc/>
    public bool Execute(IListPromptTarget target, in KeyEvent key) => target.CycleSearchMatch();
}

/// <summary>Appends the typed rune to the incremental search (any typed char).</summary>
internal sealed record ListPromptSearchTypeCommand : IListPromptCommand
{
    /// <inheritdoc/>
    public bool CanExecute(IListPromptTarget target, in KeyEvent key) => !Rune.IsControl(key.Character);

    /// <inheritdoc/>
    public bool Execute(IListPromptTarget target, in KeyEvent key) =>
        target.ApplySearch(target.SearchText + char.ConvertFromUtf32(key.Character.Value));
}

/// <summary>
/// One row of the list prompt's command table: the gesture that fires the
/// command, the scope the command is live in, and the command itself.
/// </summary>
/// <param name="Gesture">The gesture that fires the command.</param>
/// <param name="Scope">The mode the command is live in.</param>
/// <param name="Command">The command to run.</param>
internal sealed record ListPromptBinding(
    ListPromptGesture Gesture,
    ListPromptScope Scope,
    IListPromptCommand Command);

/// <summary>
/// The list prompt's command table: gesture → command, in lookup order (#1547).
/// </summary>
/// <remarks>
/// One row per gesture, in the order the old <c>switch</c> evaluated its
/// arms: the navigation keys first (live in every mode), then the
/// incremental-search commands (live only while the search is on), then the
/// vim commands (live only while it is off). The vim rows reuse the SAME
/// command objects as the arrow keys — <c>j</c>/<c>J</c> is the same move as
/// Down, <c>g</c> the same as Home — which is the duplication the old
/// <c>switch</c> + <c>UpdateVim</c> pair carried.
/// </remarks>
internal static class ListPromptCommandTable
{
    /// <summary>
    /// The move commands, shared by the arrow rows and the vim rows: the
    /// vim keys are not a second implementation of "move", they are the
    /// same command behind a second gesture.
    /// </summary>
    private static readonly ListPromptMoveByCommand MoveDown = new(1);
    private static readonly ListPromptMoveByCommand MoveUp = new(-1);
    private static readonly ListPromptMoveToEdgeCommand MoveToFirst = new(true);
    private static readonly ListPromptMoveToEdgeCommand MoveToLast = new(false);

    /// <summary>The table: the first row whose gesture matches and whose
    /// command may run is the one that runs.</summary>
    internal static readonly ListPromptBinding[] Bindings =
    [
        // ── navigation: live in every mode ────────────────────────────────
        new(new ListPromptGesture(KeyCode.Up), ListPromptScope.Any, MoveUp),
        new(new ListPromptGesture(KeyCode.Down), ListPromptScope.Any, MoveDown),
        new(new ListPromptGesture(KeyCode.Home), ListPromptScope.Any, MoveToFirst),
        new(new ListPromptGesture(KeyCode.End), ListPromptScope.Any, MoveToLast),
        new(new ListPromptGesture(KeyCode.PageUp), ListPromptScope.Any, new ListPromptPageByCommand(-1)),
        new(new ListPromptGesture(KeyCode.PageDown), ListPromptScope.Any, new ListPromptPageByCommand(1)),

        // ── incremental search: live only while the search is on ───────────
        new(new ListPromptGesture(KeyCode.Backspace), ListPromptScope.Search, new ListPromptSearchBackspaceCommand()),
        new(new ListPromptGesture(KeyCode.Tab), ListPromptScope.Search, new ListPromptSearchCycleCommand()),
        new(new ListPromptGesture(KeyCode.Char, TypedOnly: true), ListPromptScope.Search, new ListPromptSearchTypeCommand()),

        // ── vim keys: live only while the search is off ────────────────────
        new(new ListPromptGesture(KeyCode.Char, new Rune('j'), TypedOnly: true), ListPromptScope.Navigation, MoveDown),
        new(new ListPromptGesture(KeyCode.Char, new Rune('J'), TypedOnly: true), ListPromptScope.Navigation, MoveDown),
        new(new ListPromptGesture(KeyCode.Char, new Rune('k'), TypedOnly: true), ListPromptScope.Navigation, MoveUp),
        new(new ListPromptGesture(KeyCode.Char, new Rune('K'), TypedOnly: true), ListPromptScope.Navigation, MoveUp),
        new(new ListPromptGesture(KeyCode.Char, new Rune('g'), TypedOnly: true), ListPromptScope.Navigation, MoveToFirst),
        new(new ListPromptGesture(KeyCode.Char, new Rune('G'), TypedOnly: true), ListPromptScope.Navigation, MoveToLast),
    ];
}

/// <summary>
/// Navigable list state (Spectre.Console <c>ListPromptState{T}</c> ported onto
/// CellForge <see cref="KeyEvent"/> input): leaf-index table, wrap-around,
/// incremental search (type/Backspace/Tab-cycle), vim keys, and paging.
/// UI-only state — no rendering, no console; the host paints and owns commit.
/// Empty-safe throughout (<see cref="Index"/> is -1, every mutator is a no-op
/// returning false) — upstream assumes a non-empty list, our pickers do not.
/// </summary>
/// <typeparam name="T">Leaf payload type.</typeparam>
public sealed class ListPromptState<T> : IListPromptTarget where T : notnull
{
    private readonly Func<T, string> _converter;
    private readonly List<int>? _leafIndexes;

    /// <summary>Flat rows (headers interleaved). The host must not mutate it behind the state.</summary>
    public IReadOnlyList<ListPromptItem<T>> Items { get; }

    /// <summary>Number of flat rows.</summary>
    public int ItemCount => Items.Count;

    /// <summary>Cursor as a flat-row index (-1 when empty).</summary>
    public int Index { get; private set; }

    /// <summary>Rows one PageUp/PageDown jumps.</summary>
    public int PageSize { get; }

    /// <summary>When true, stepping past an end wraps to the other end.</summary>
    public bool WrapAround { get; }

    /// <summary>How group headers participate in navigation.</summary>
    public ListSelectionMode Mode { get; }

    /// <summary>When true, typing filters by jumping to the first match.</summary>
    public bool SearchEnabled { get; }

    /// <summary>Current incremental-search text (empty when search is off or cleared).</summary>
    public string SearchText { get; private set; } = string.Empty;

    public ListPromptState(
        IReadOnlyList<ListPromptItem<T>> items,
        Func<T, string>? converter = null,
        int pageSize = 5,
        bool wrapAround = false,
        ListSelectionMode mode = ListSelectionMode.Single,
        bool searchEnabled = false,
        int initialIndex = 0)
    {
        Ensure.NotNull(items);
        Items = items;
        _converter = converter ?? (static v => v.ToString() ?? string.Empty);
        PageSize = Math.Max(1, pageSize);
        WrapAround = wrapAround;
        Mode = mode;
        SearchEnabled = searchEnabled;

        if (Items.Count == 0)
        {
            Index = -1;
            return;
        }

        if (mode != ListSelectionMode.Leaf)
        {
            Index = Math.Clamp(initialIndex, 0, Items.Count - 1);
            return;
        }

        _leafIndexes = [];
        for (int i = 0; i < Items.Count; i++)
        {
            if (!Items[i].IsGroup)
            {
                _leafIndexes.Add(i);
            }
        }

        Index = _leafIndexes.Contains(initialIndex) && initialIndex >= 0
            ? initialIndex
            : (_leafIndexes.Count > 0 ? _leafIndexes[0] : -1);
    }

    /// <summary>Builds a flat leaf-only state from plain values (the
    /// <see cref="DialogSelectList"/> shape: no groups, no search).</summary>
    public static ListPromptState<T> FromValues(
        IReadOnlyList<T> values,
        Func<T, string>? converter = null,
        int pageSize = 5,
        bool wrapAround = false,
        ListSelectionMode mode = ListSelectionMode.Single,
        bool searchEnabled = false,
        int initialIndex = 0)
    {
        Ensure.NotNull(values);
        var nodes = new List<ListPromptItem<T>>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            nodes.Add(new ListPromptItem<T>(values[i]));
        }

        return new ListPromptState<T>(nodes, converter, pageSize, wrapAround, mode, searchEnabled, initialIndex);
    }

    /// <summary>Payload under the cursor (false when empty).</summary>
    public bool TryGetCurrent(out T value)
    {
        if (Index >= 0 && Index < Items.Count)
        {
            value = Items[Index].Data;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Steps the cursor by a signed delta in the active (leaf or flat) space.</summary>
    public bool MoveBy(int delta)
    {
        if (Items.Count == 0 || delta == 0 || Index < 0)
        {
            return false;
        }

        if (Mode != ListSelectionMode.Leaf)
        {
            return SetFlatIndex(WrapAround ? Wrap(Index + delta, Items.Count) : Math.Clamp(Index + delta, 0, Items.Count - 1));
        }

        if (_leafIndexes is null || _leafIndexes.Count == 0)
        {
            return false;
        }

        int pos = _leafIndexes.IndexOf(Index);
        if (pos < 0)
        {
            pos = 0;
        }

        int next = WrapAround ? Wrap(pos + delta, _leafIndexes.Count) : Math.Clamp(pos + delta, 0, _leafIndexes.Count - 1);
        return SetFlatIndex(_leafIndexes[next]);
    }

    /// <summary>Parks the cursor on a flat row (nearest leaf at-or-after, else before, in Leaf mode;
    /// Multiple parks on headers too, so groups stay toggleable).</summary>
    public bool MoveTo(int flatIndex)
    {
        if (Items.Count == 0 || Index < 0)
        {
            return false;
        }

        int target = Math.Clamp(flatIndex, 0, Items.Count - 1);
        if (Mode == ListSelectionMode.Leaf && Items[target].IsGroup)
        {
            target = NearestLeaf(target);
            if (target < 0)
            {
                return false;
            }
        }

        return SetFlatIndex(target);
    }

    /// <summary>Jumps by whole pages (one page = <see cref="PageSize"/> rows).</summary>
    public bool PageBy(int pages) => MoveBy(pages * PageSize);

    /// <summary>
    /// Handles one key through the command table
    /// (<see cref="ListPromptCommandTable"/>): the navigation commands
    /// (arrows/Home/End/paging) in every mode, the incremental-search
    /// commands (type/Backspace/Tab-cycle) while the search is on, the vim
    /// commands (j/J/k/K/g/G) while it is off. Returns true when the cursor
    /// or the search text moved.
    /// </summary>
    /// <remarks>
    /// Intentional deviation from upstream: Spectre.Console cannot tell a
    /// <c>K</c> console key from a typed <c>k</c>, so <c>k</c> both moves and
    /// extends the search. Our <see cref="KeyCode.Char"/>/arrow split lets us
    /// keep query typing (palette, file picker) free of cursor jumps.
    /// </remarks>
    public bool Update(in KeyEvent key)
    {
        if (Items.Count == 0 || Index < 0)
        {
            return false;
        }

        ListPromptScope scope = SearchEnabled ? ListPromptScope.Search : ListPromptScope.Navigation;
        ListPromptBinding[] bindings = ListPromptCommandTable.Bindings;

        for (int i = 0; i < bindings.Length; i++)
        {
            ListPromptBinding binding = bindings[i];

            if (binding.Scope != ListPromptScope.Any && binding.Scope != scope)
            {
                continue;
            }

            if (!binding.Gesture.Matches(in key) || !binding.Command.CanExecute(this, in key))
            {
                continue;
            }

            return binding.Command.Execute(this, in key);
        }

        return false;
    }

    /// <summary>
    /// Flips the checkmark under the cursor (<see cref="ListSelectionMode.Multiple"/>
    /// only): a group flips its whole subtree, then ancestors are recounted
    /// (selected exactly when all children are). Returns true when anything changed.
    /// </summary>
    public bool ToggleCurrent()
    {
        if (Mode != ListSelectionMode.Multiple || Index < 0 || Index >= Items.Count)
        {
            return false;
        }

        var node = Items[Index];
        if (node.IsGroup)
        {
            bool target = !SubtreeSelected(node);
            SetSubtree(node, target);
            node.IsSelected = target; // the group recounts to the same target its subtree just took
        }
        else
        {
            node.IsSelected = !node.IsSelected;
        }

        RecountAncestors(node);
        return true;
    }

    /// <summary>Selected leaf payloads in flat order (the multi-mode commit set).</summary>
    public List<T> GetSelectedLeaves()
    {
        var selected = new List<T>();
        for (int i = 0; i < Items.Count; i++)
        {
            if (!Items[i].IsGroup && Items[i].IsSelected)
            {
                selected.Add(Items[i].Data);
            }
        }

        return selected;
    }

    /// <summary>
    /// Paging-window adjuster for hosts that own a <see cref="ScrollableViewport"/>:
    /// given its current offset and painted height, returns the offset that keeps
    /// the cursor visible (clamped). Feed the result back into
    /// <c>ScrollableViewport.SetOffset</c>.
    /// </summary>
    public int ScrollOffsetFor(int currentOffset, int viewportH)
    {
        if (Items.Count == 0 || viewportH <= 0 || Index < 0)
        {
            return 0;
        }

        int max = Math.Max(0, Items.Count - viewportH);
        int offset = Math.Clamp(currentOffset, 0, max);
        if (Index < offset)
        {
            offset = Index;
        }
        else if (Index >= offset + viewportH)
        {
            offset = Index - viewportH + 1;
        }

        return offset;
    }

    private bool SetFlatIndex(int flatIndex)
    {
        if (flatIndex == Index)
        {
            return false;
        }

        Index = flatIndex;
        return true;
    }

    /// <summary>
    /// Parks the cursor on the first/last navigable row: the first/last leaf
    /// in Leaf mode, the first/last flat row otherwise (Multiple parks on
    /// headers too, so groups stay toggleable).
    /// </summary>
    /// <remarks>
    /// The command-facing member of <see cref="IListPromptTarget"/>: Home/End
    /// and the vim g/G all land here, which is why the old per-key
    /// <c>MoveToLeafOrFlat</c> call sites disappeared.
    /// </remarks>
    bool IListPromptTarget.MoveToEdge(bool toStart)
    {
        if (Mode != ListSelectionMode.Leaf)
        {
            return SetFlatIndex(Math.Clamp(toStart ? 0 : Items.Count - 1, 0, Items.Count - 1));
        }

        if (_leafIndexes is null || _leafIndexes.Count == 0)
        {
            return false;
        }

        return SetFlatIndex(toStart ? _leafIndexes[0] : _leafIndexes[^1]);
    }

    private int NearestLeaf(int flatIndex)
    {
        for (int i = flatIndex; i < Items.Count; i++)
        {
            if (!Items[i].IsGroup)
            {
                return i;
            }
        }

        for (int i = flatIndex - 1; i >= 0; i--)
        {
            if (!Items[i].IsGroup)
            {
                return i;
            }
        }

        return -1;
    }

    private bool MatchesSearch(ListPromptItem<T> node, string search)
    {
        if (node.IsGroup && Mode != ListSelectionMode.Single)
        {
            return false;
        }

        return _converter(node.Data).Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies a search string: jumps to the first matching row and stores
    /// the text. The command-facing member of <see cref="IListPromptTarget"/>.
    /// </summary>
    bool IListPromptTarget.ApplySearch(string search)
    {
        int index = Index;
        for (int i = 0; i < Items.Count; i++)
        {
            if (MatchesSearch(Items[i], search))
            {
                index = i;
                break;
            }
        }

        if (index == Index && search == SearchText)
        {
            return false;
        }

        Index = index;
        SearchText = search;
        return true;
    }

    /// <summary>
    /// Cycles to the next matching row for the current search text. The
    /// command-facing member of <see cref="IListPromptTarget"/>.
    /// </summary>
    bool IListPromptTarget.CycleSearchMatch()
    {
        var matches = new List<int>();
        for (int i = 0; i < Items.Count; i++)
        {
            if (MatchesSearch(Items[i], SearchText))
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            return false;
        }

        int pos = matches.IndexOf(Index);
        int next = pos < 0 ? matches[0] : matches[(pos + 1) % matches.Count];
        if (next == Index)
        {
            return false;
        }

        Index = next;
        return true;
    }

    private static bool SubtreeSelected(ListPromptItem<T> group)
    {
        foreach (var node in group.Traverse(includeSelf: false))
        {
            if (!node.IsGroup && !node.IsSelected)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetSubtree(ListPromptItem<T> group, bool selected)
    {
        foreach (var node in group.Traverse(includeSelf: false))
        {
            if (!node.IsGroup)
            {
                node.IsSelected = selected;
            }
        }
    }

    private static void RecountAncestors(ListPromptItem<T> node)
    {
        var parent = node.Parent;
        while (parent is not null)
        {
            bool all = true;
            for (int i = 0; i < parent.Children.Count; i++)
            {
                if (!parent.Children[i].IsSelected)
                {
                    all = false;
                    break;
                }
            }

            parent.IsSelected = all;
            parent = parent.Parent;
        }
    }

    private static int Wrap(int value, int count) => ((value % count) + count) % count;
}

/// <summary>
/// Per-mode key policy over a <see cref="ListPromptState{T}"/> (Spectre.Console
/// <c>IListPromptStrategy{T}</c> minus the console-bound halves:
/// <c>CalculatePageSize</c> takes plain counts, and rendering stays in the
/// widget — there is no <c>Render</c> here).
/// </summary>
/// <typeparam name="T">Leaf payload type.</typeparam>
public interface IListPromptStrategy<T> where T : notnull
{
    /// <summary>Maps one key to a prompt action (Enter→Submit, Esc→Abort, navigation→Refresh).</summary>
    ListPromptInputResult HandleInput(in KeyEvent key, ListPromptState<T> state);

    /// <summary>Clamps a requested page size to <c>[1 .. max(1, total)]</c> (1 when empty).</summary>
    int CalculatePageSize(int totalItemCount, int requestedPageSize);

    /// <summary>Initial cursor for a node list (first leaf in Leaf/Multiple, 0 in Single, -1 when empty).</summary>
    int CalculateInitialIndex(IReadOnlyList<ListPromptItem<T>> nodes);
}

/// <summary>Single-commit policy: Enter commits, Esc aborts, the rest navigates/searches.</summary>
/// <typeparam name="T">Leaf payload type.</typeparam>
public sealed class SingleListPromptStrategy<T> : IListPromptStrategy<T> where T : notnull
{
    /// <inheritdoc/>
    public ListPromptInputResult HandleInput(in KeyEvent key, ListPromptState<T> state)
    {
        Ensure.NotNull(state);
        if (key.Key == KeyCode.Enter)
        {
            return ListPromptInputResult.Submit;
        }

        if (key.Key == KeyCode.Escape)
        {
            return ListPromptInputResult.Abort;
        }

        return state.Update(in key) ? ListPromptInputResult.Refresh : ListPromptInputResult.None;
    }

    /// <inheritdoc/>
    public int CalculatePageSize(int totalItemCount, int requestedPageSize) =>
        totalItemCount <= 0 ? 1 : Math.Clamp(requestedPageSize, 1, totalItemCount);

    /// <inheritdoc/>
    public int CalculateInitialIndex(IReadOnlyList<ListPromptItem<T>> nodes)
    {
        Ensure.NotNull(nodes);
        return nodes.Count == 0 ? -1 : 0;
    }
}

/// <summary>Multi-select policy: Space toggles (with parent recount), Enter commits, Esc aborts.</summary>
/// <typeparam name="T">Leaf payload type.</typeparam>
public sealed class MultiListPromptStrategy<T> : IListPromptStrategy<T> where T : notnull
{
    /// <inheritdoc/>
    public ListPromptInputResult HandleInput(in KeyEvent key, ListPromptState<T> state)
    {
        Ensure.NotNull(state);
        if (key.Key == KeyCode.Enter)
        {
            return ListPromptInputResult.Submit;
        }

        if (key.Key == KeyCode.Escape)
        {
            return ListPromptInputResult.Abort;
        }

        // Space toggles only outside search: with search on it is query text
        // (queries contain spaces), mirroring the vim-key/search split in Update.
        if (key.Key == KeyCode.Char && key.Character.Value == ' ' && !state.SearchEnabled)
        {
            return state.ToggleCurrent() ? ListPromptInputResult.Refresh : ListPromptInputResult.None;
        }

        return state.Update(in key) ? ListPromptInputResult.Refresh : ListPromptInputResult.None;
    }

    /// <inheritdoc/>
    public int CalculatePageSize(int totalItemCount, int requestedPageSize) =>
        totalItemCount <= 0 ? 1 : Math.Clamp(requestedPageSize, 1, totalItemCount);

    /// <inheritdoc/>
    public int CalculateInitialIndex(IReadOnlyList<ListPromptItem<T>> nodes)
    {
        Ensure.NotNull(nodes);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!nodes[i].IsGroup)
            {
                return i;
            }
        }

        return nodes.Count == 0 ? -1 : 0;
    }
}
