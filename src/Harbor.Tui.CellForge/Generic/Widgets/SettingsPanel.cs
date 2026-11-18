// #2380: domain-independent settings panel from gpui-kit-components.md (Settings).
//
// The gpui-kit component is a host of game-specific pages, so only the
// domain-free shape is ported: host-prepared records (page -> group -> entry),
// a controlled value channel and a validation hook. This file names no Harbor
// config type, no plugin seam and no panel id — the host owns the records, the
// values and the decision to mount the widget.
//
// Values travel as strings for every editor (toggle "0"/"1", choice = option
// text, text = typed text), mirroring the one-channel design of
// GenericToggleSelect. Text editing reuses GenericTextField, so rune-aware
// insert/backspace/cursor motion is not reimplemented here.
//
// Key routing is state dependent: navigation refuses Shift (command gate),
// while an open text editor admits Shift as a case shaper (buffer gate). Both
// families are declared in KeyGateFamilyRules.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>How the value of a <see cref="SettingEntry"/> is edited.</summary>
public enum SettingEditorKind
{
    /// <summary>Binary value: "0" or "1".</summary>
    Toggle,

    /// <summary>One of <see cref="SettingEntry.Options"/>, cycled by activation.</summary>
    Choice,

    /// <summary>Free text, edited through <see cref="GenericTextField"/>.</summary>
    Text,
}

/// <summary>
/// A host-prepared setting. The panel stores the record as given and hands the
/// same instance to the validation hook and the change callback; it never
/// reaches into a config service to build one.
/// </summary>
/// <param name="Id">Stable identity used by the host's value stores.</param>
/// <param name="Label">Text painted beside the value.</param>
/// <param name="Editor">Which editing behaviour applies.</param>
/// <param name="Options">Choice values, in cycle order. Empty for other editors.</param>
public sealed record SettingEntry(
    string Id,
    string Label,
    SettingEditorKind Editor,
    IReadOnlyList<string> Options)
{
    /// <summary>Creates an entry with no options.</summary>
    public SettingEntry(string id, string label, SettingEditorKind editor)
        : this(id, label, editor, Array.Empty<string>())
    {
    }

    /// <summary>Creates a binary entry.</summary>
    public static SettingEntry Toggle(string id, string label) =>
        new(id, label, SettingEditorKind.Toggle);

    /// <summary>Creates a single-choice entry.</summary>
    public static SettingEntry Choice(string id, string label, IReadOnlyList<string> options) =>
        new(id, label, SettingEditorKind.Choice, options);

    /// <summary>Creates a free-text entry.</summary>
    public static SettingEntry Text(string id, string label) =>
        new(id, label, SettingEditorKind.Text);
}

/// <summary>A titled group of settings on one page.</summary>
/// <param name="Title">Group heading painted above its entries.</param>
/// <param name="Entries">Entries in paint and navigation order.</param>
public sealed record SettingsGroup(string Title, IReadOnlyList<SettingEntry> Entries);

/// <summary>A page of settings groups. Left/Right (or PageUp/PageDown) switch pages.</summary>
/// <param name="Id">Stable identity used by the host.</param>
/// <param name="Title">Tab caption.</param>
/// <param name="Groups">Groups in paint order.</param>
public sealed record SettingsPage(string Id, string Title, IReadOnlyList<SettingsGroup> Groups);

/// <summary>
/// Host-agnostic settings panel over prepared records. The host owns the values
/// through <c>getValue</c>/<c>onChange</c> and may supply a <c>validate</c> hook
/// that receives the prepared entry and the candidate value, returning an
/// error string or null. The widget holds only navigation state, the current
/// text draft and the last validation error.
/// </summary>
/// <remarks>
/// It implements the engine <see cref="Harbor.Tui.CellForge.Input.IFocusTarget"/>,
/// so a host joins it to a <see cref="Harbor.Tui.CellForge.Input.FocusRouter"/>
/// Tab order. Keys arrive through <see cref="HandleKey"/>. No extension axis,
/// plugin seam or renderer registration is added by this type.
/// </remarks>
public sealed class SettingsPanel : Harbor.Tui.CellForge.Input.IFocusTarget
{
    private static readonly CellStyle TextStyle = new(ChatPalette.Text);
    private static readonly CellStyle MutedStyle = new(ChatPalette.Muted, attrs: StyleAttr.Dim);
    private static readonly CellStyle ActiveStyle = new(ChatPalette.Accent, attrs: StyleAttr.Bold);
    private static readonly CellStyle ErrorStyle = new(ChatPalette.Error, attrs: StyleAttr.Bold);

    private readonly SettingsPage[] _pages;
    private readonly Func<SettingEntry, string> _getValue;
    private readonly Action<SettingEntry, string> _onChange;
    private readonly Func<SettingEntry, string, string?>? _validate;
    private readonly GenericTextField _editor = new();

    private SettingEntry[] _entries = Array.Empty<SettingEntry>();
    private int _pageIndex;
    private int _entryIndex;
    private string? _error;
    private bool _editing;
    private bool _disabled;

    /// <summary>
    /// Creates a panel over the host's prepared pages.
    /// </summary>
    /// <param name="id">Stable focus id.</param>
    /// <param name="pages">Pages in tab order.</param>
    /// <param name="getValue">Reads the current value of an entry.</param>
    /// <param name="onChange">Called only after validation passes.</param>
    /// <param name="validate">Optional hook: null means valid, otherwise an error message.</param>
    public SettingsPanel(
        string id,
        IReadOnlyList<SettingsPage> pages,
        Func<SettingEntry, string> getValue,
        Action<SettingEntry, string> onChange,
        Func<SettingEntry, string, string?>? validate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(getValue);
        ArgumentNullException.ThrowIfNull(onChange);

        Id = id;
        _pages = new SettingsPage[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            _pages[i] = pages[i];
        }

        _getValue = getValue;
        _onChange = onChange;
        _validate = validate;
        LoadPage(0);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Prepared pages, in tab order.</summary>
    public IReadOnlyList<SettingsPage> Pages => _pages;

    /// <summary>Index of the visible page.</summary>
    public int PageIndex => _pageIndex;

    /// <summary>Number of prepared pages.</summary>
    public int PageCount => _pages.Length;

    /// <summary>Visible page, or null when no pages were supplied.</summary>
    public SettingsPage? Page => _pages.Length == 0 ? null : _pages[_pageIndex];

    /// <summary>The visible page's entries, flattened across its groups.</summary>
    public IReadOnlyList<SettingEntry> Entries => _entries;

    /// <summary>Cursor into <see cref="Entries"/>.</summary>
    public int Index => _entryIndex;

    /// <summary>Entry under the cursor, or null when the page has no entries.</summary>
    public SettingEntry? CurrentEntry => _entries.Length == 0 ? null : _entries[_entryIndex];

    /// <summary>Current value of <see cref="CurrentEntry"/>, read from the host.</summary>
    public string CurrentValue
    {
        get
        {
            var entry = CurrentEntry;
            return entry is null ? string.Empty : _getValue(entry) ?? string.Empty;
        }
    }

    /// <summary>Last validation error, or null when the last edit was accepted.</summary>
    public string? Error => _error;

    /// <summary>True while a free-text entry is being edited.</summary>
    public bool IsEditing => _editing;

    /// <summary>Current text draft (empty outside edit mode).</summary>
    public string Draft => _editing ? _editor.Text : string.Empty;

    /// <summary>
    /// Disabled panels report <see cref="CanFocus"/> = false and refuse every
    /// key, so neither onChange nor validate can fire for them.
    /// </summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            if (_disabled == value)
            {
                return;
            }
            _disabled = value;
            if (value)
            {
                _editing = false;
                _error = null;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether this panel may own focus right now.</summary>
    public bool CanFocus => !_disabled;

    /// <summary>Whether this panel currently owns focus.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>Raised when navigation, editing, validation or enabled state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>FocusRouter callback: track focus so Paint can mark the active page.</summary>
    public void OnFocusChanged(bool focused)
    {
        IsFocused = focused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a page and resets the entry cursor. False when there are no pages.</summary>
    public bool SetPage(int index)
    {
        if (_pages.Length == 0)
        {
            return false;
        }

        int next = Math.Clamp(index, 0, _pages.Length - 1);
        bool moved = next != _pageIndex;
        LoadPage(next);
        if (moved)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>Moves the entry cursor by a signed delta, clamped to the page.</summary>
    public bool Move(int delta)
    {
        if (_entries.Length == 0)
        {
            return false;
        }

        int next = Math.Clamp(_entryIndex + delta, 0, _entries.Length - 1);
        bool changed = next != _entryIndex || _editing || _error is not null;
        _entryIndex = next;
        _editing = false;
        _error = null;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>Moves the entry cursor to an absolute index, clamped to the page.</summary>
    public bool MoveTo(int index)
    {
        if (_entries.Length == 0)
        {
            return false;
        }

        int next = Math.Clamp(index, 0, _entries.Length - 1);
        bool changed = next != _entryIndex || _editing || _error is not null;
        _entryIndex = next;
        _editing = false;
        _error = null;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>
    /// Routes one key. Outside edit mode this is a command gate (no modifiers):
    /// Up/Down move the entry cursor, Left/Right or PageUp/PageDown switch
    /// pages, Home/End jump, Enter or Space activate. In edit mode typed runes
    /// and caret keys reach <see cref="GenericTextField"/> (Shift admitted),
    /// Enter commits through the validation hook and Escape cancels.
    /// </summary>
    public bool HandleKey(KeyEvent key)
    {
        if (_disabled || key.EventType is not (KeyEventType.Press or KeyEventType.Repeat))
        {
            return false;
        }

        if (_editing)
        {
            return HandleEditingKey(key);
        }

        if (!key.Modifiers.IsUnmodified())
        {
            return false;
        }

        bool space = key.Key == KeyCode.Char && key.Character.Value == ' ';
        switch (key.Key)
        {
            case KeyCode.Up:
                return Move(-1);
            case KeyCode.Down:
                return Move(1);
            case KeyCode.Home:
                return MoveTo(0);
            case KeyCode.End:
                return MoveTo(_entries.Length - 1);
            case KeyCode.Left:
            case KeyCode.PageUp:
                return SetPage(_pageIndex - 1);
            case KeyCode.Right:
            case KeyCode.PageDown:
                return SetPage(_pageIndex + 1);
            case KeyCode.Enter:
                return Activate();
            default:
                return space && Activate();
        }
    }

    private bool HandleEditingKey(KeyEvent key)
    {
        if (!key.Modifiers.AcceptsTypedChar())
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Escape:
                _editing = false;
                _error = null;
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            case KeyCode.Enter:
            case KeyCode.Tab:
                return TryCommit(_editor.Text);
            case KeyCode.Char:
                bool typed = _editor.HandleKey(key);
                if (typed)
                {
                    _error = null;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                return typed;
            case KeyCode.Backspace:
            case KeyCode.Delete:
            case KeyCode.Left:
            case KeyCode.Right:
            case KeyCode.Home:
            case KeyCode.End:
                bool moved = _editor.HandleKey(key);
                if (moved)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                return moved;
            default:
                return false;
        }
    }

    private bool Activate()
    {
        var entry = CurrentEntry;
        if (entry is null)
        {
            return false;
        }

        switch (entry.Editor)
        {
            case SettingEditorKind.Toggle:
                return TryCommit(CurrentValue == "1" ? "0" : "1");
            case SettingEditorKind.Choice:
                return TryCommit(NextChoice(entry));
            case SettingEditorKind.Text:
                _editor.SetText(CurrentValue);
                _editor.MoveCursor(_editor.Text.Length); // SetText keeps the old cursor; edits start at the end
                _editor.IsFocused = true;
                _editing = true;
                _error = null;
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    private string NextChoice(SettingEntry entry)
    {
        var options = entry.Options;
        if (options is null || options.Count == 0)
        {
            return CurrentValue;
        }
        int count = options.Count;
        int current = -1;
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(options[i], CurrentValue, StringComparison.Ordinal))
            {
                current = i;
                break;
            }
        }
        int next = (current + 1) % count;
        return options[next] ?? string.Empty;
    }

    private bool TryCommit(string next)
    {
        var entry = CurrentEntry;
        if (entry is null)
        {
            return false;
        }

        string? error = _validate?.Invoke(entry, next);
        if (error is not null)
        {
            _error = error;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        _error = null;
        _onChange(entry, next);
        _editing = false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Paints tabs, group headings and entry rows into <paramref name="rect"/>.
    /// The cursor row is marked with '>', a rejected edit adds an error row,
    /// and an open text editor paints through <see cref="GenericTextField"/> so
    /// the caret and scroll stay rune-aware.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int row = rect.Y;
        int maxY = rect.Y + rect.Height;

        void WriteRow(string text, in CellStyle style)
        {
            if (row >= maxY)
            {
                return;
            }
            Write(buffer, rect.X, row, text, rect.Width, style);
            row++;
        }

        int tabX = rect.X;
        for (int p = 0; p < _pages.Length && tabX < rect.X + rect.Width; p++)
        {
            bool active = p == _pageIndex;
            string tab = (active ? ">" : " ") + _pages[p].Title + "  ";
            int room = rect.X + rect.Width - tabX;
            if (tab.Length > room)
            {
                tab = tab[..room];
            }
            if (tab.Length == 0)
            {
                break;
            }
            Write(buffer, tabX, row, tab, tab.Length, active ? ActiveStyle : MutedStyle);
            tabX += tab.Length;
        }
        row++;

        var page = Page;
        if (page is null)
        {
            WriteRow("No settings", MutedStyle);
            return;
        }

        int entryNumber = 0;
        foreach (var group in page.Groups)
        {
            WriteRow("## " + group.Title, MutedStyle);
            foreach (var entry in group.Entries)
            {
                if (row >= maxY)
                {
                    return;
                }

                bool current = entryNumber == _entryIndex;
                string prefix = (current ? "> " : "  ") + entry.Label + ": ";
                int prefixLen = prefix.Length;
                Write(buffer, rect.X, row, prefix, rect.Width, current ? ActiveStyle : TextStyle);

                if (current)
                {
                    buffer.SetStyleAt(rect.X, row, ActiveStyle);
                }

                if (_editing && current)
                {
                    int valueWidth = rect.Width - prefixLen;
                    if (valueWidth > 0)
                    {
                        _editor.Paint(buffer, new Rect(rect.X + prefixLen, row, valueWidth, 1));
                    }
                }
                else
                {
                    string value = DisplayValue(entry);
                    if (prefixLen < rect.Width)
                    {
                        Write(buffer, rect.X + prefixLen, row, value, rect.Width - prefixLen, TextStyle);
                    }
                }
                row++;

                if (current && _error is not null && row < maxY)
                {
                    WriteRow("  ! " + _error, ErrorStyle);
                }
                entryNumber++;
            }
        }
    }

    private string DisplayValue(SettingEntry entry)
    {
        string value = CurrentValue;
        if (entry.Editor == SettingEditorKind.Toggle)
        {
            return value == "1" ? "[x]" : "[ ]";
        }
        return value;
    }

    private void LoadPage(int index)
    {
        _pageIndex = _pages.Length == 0 ? 0 : Math.Clamp(index, 0, _pages.Length - 1);
        var flat = new List<SettingEntry>();
        if (_pages.Length > 0)
        {
            foreach (var group in _pages[_pageIndex].Groups)
            {
                foreach (var entry in group.Entries)
                {
                    flat.Add(entry);
                }
            }
        }
        _entries = flat.ToArray();
        _entryIndex = 0;
        _editing = false;
        _error = null;
    }

    private static void Write(ScreenBuffer buffer, int x, int y, string text, int width, in CellStyle style)
    {
        if (width <= 0 || text.Length == 0)
        {
            return;
        }
        string clipped = text.Length > width ? text[..width] : text;
        buffer.SetText(x, y, clipped, style);
    }
}
