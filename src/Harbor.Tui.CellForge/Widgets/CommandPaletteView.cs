using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Navigation;

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

/// <summary>
/// CF-E-017: icon-key → glyph mapping for builtin palette items.
/// Mirrors the spirit of <see cref="ToolCallBlock" /> (const glyphs + plain
/// ASCII fallbacks for terminals without Nerd Font). Unknown / missing keys
/// map to <see cref="string.Empty" /> (plain-text, no throw) so a foreign
/// template can never crash the palette.
/// </summary>
public static class PaletteIconMap
{
    /// <summary>ASCII fallback per icon key (e + &gt; ~ $ ? # @ o *).</summary>
    public static string ToAscii(string? iconKey) => iconKey switch
    {
        "FolderIcon" => "e",
        "PlusIcon" => "+",
        "BranchIcon" => ">",
        "ThemeIcon" => "~",
        "CodeIcon" => "$",
        "DiffIcon" => "?",
        "ChartIcon" => "#",
        "SettingsIcon" => "@",
        "ProviderIcon" => "o",
        "QuitIcon" => "*",
        _ => string.Empty,
    };

    /// <summary>Nerd Font (FontAwesome PUA) glyph per icon key.</summary>
    public static string ToNerdFont(string? iconKey) => iconKey switch
    {
        "FolderIcon" => "",
        "PlusIcon" => "",
        "BranchIcon" => "",
        "ThemeIcon" => "",
        "CodeIcon" => "",
        "DiffIcon" => "",
        "ChartIcon" => "",
        "SettingsIcon" => "",
        "ProviderIcon" => "",
        "QuitIcon" => "",
        _ => string.Empty,
    };

    /// <summary>
    /// Resolves an icon key to a single-glyph prefix. Unknown, null or
    /// whitespace keys return <see cref="string.Empty" /> (plain-text fallback).
    /// </summary>
    /// <param name="iconKey">Icon key from the builtin template (e.g. <c>FolderIcon</c>).</param>
    /// <param name="useNerdFont">When true and a Nerd glyph exists, return it; otherwise the ASCII fallback.</param>
    public static string Resolve(string? iconKey, bool useNerdFont = false)
    {
        if (string.IsNullOrWhiteSpace(iconKey))
        {
            return string.Empty;
        }

        string key = iconKey.Trim();
        if (useNerdFont)
        {
            string nerd = ToNerdFont(key);
            if (nerd.Length > 0)
            {
                return nerd;
            }
        }

        return ToAscii(key);
    }
}

/// <summary>
/// CF-E-017: cell-local mirror of the desktop command catalogs.
/// Literals are kept 1:1 with
/// <c>src/Harbor.Desktop.Shared/Commands/SlashCommands.cs</c> (<c>All</c>, 10 entries)
/// and <c>src/Harbor.Desktop.Shared/Commands/BuiltInCommands.cs</c> (<c>Templates()</c>, 10 entries).
/// No project reference to <c>Harbor.Desktop.Shared</c> is taken on purpose:
/// the architecture matrix forbids a <c>Harbor.Tui.CellForge → Harbor.Desktop.Shared</c> edge,
/// so the palette owns a literal copy and documents the source.
/// Existing palette behavior (fuzzy via <see cref="FuzzyMatcher" />, groups,
/// navigation, <see cref="CommandPaletteView.OnCommit" />) is untouched — only item sources are added.
/// </summary>
public static class CommandPaletteCatalog
{
    private sealed record SlashDef(string Name, string Description, string[] Aliases);

    private sealed record BuiltinDef(string Title, string Subtitle, string IconKey, string Id);

    private static readonly SlashDef[] SlashDefs =
    [
        new("/help", "Show this help screen", []),
        new("/clear", "Clear the current chat transcript", ["cls"]),
        new("/quit", "Exit Harbor", ["exit"]),
        new("/sessions", "List recent sessions", []),
        new("/branch", "Branch the current session at the last assistant message", []),
        new("/providers", "List configured providers", []),
        new("/tokens", "Show token usage for the current session", []),
        new("/theme", "Toggle between dark and light theme", []),
        new("/editor", "Open the code editor", []),
        new("/diff", "Open the diff viewer", []),
    ];

    private static readonly BuiltinDef[] BuiltinDefs =
    [
        new("Open Session", "Open an existing chat session", "FolderIcon", OverlayIds.SessionsFlyout),
        new("New Session", "Start a fresh chat session", "PlusIcon", "new-session"),
        new("Branch Session", "Branch the current session at the selected message", "BranchIcon", "branch-session"),
        new("Toggle Theme", "Switch between dark and light", "ThemeIcon", "toggle-theme"),
        new("Open Code Editor", "Open the built-in code editor", "CodeIcon", "open-code-editor"),
        new("Open Diff View", "Open the diff viewer", "DiffIcon", OverlayIds.Diff),
        new("Open Token Usage", "Show per-session token usage and cost", "ChartIcon", OverlayIds.TokenUsage),
        new("Open Settings", "Configure providers, theme, fonts", "SettingsIcon", OverlayIds.Settings),
        new("Open Provider Browser", "Browse and configure LLM providers", "ProviderIcon", OverlayIds.ProviderBrowser),
        new("Quit", "Exit Harbor", "QuitIcon", "quit"),
    ];

    /// <summary>Slash catalog (10 items, group "Slash"). Mirrors <c>SlashCommands.All</c>.</summary>
    public static IReadOnlyList<CommandItem> SlashCatalog { get; } = BuildSlashCatalog();

    /// <summary>Builds the slash catalog (10 items, group "Slash").</summary>
    public static IReadOnlyList<CommandItem> GetSlashCatalog() => SlashCatalog;

    /// <summary>
    /// Builds the builtin catalog (10 items, group "Commands").
    /// Titles carry the icon prefix (<c>"&lt;glyph&gt; &lt;title&gt;"</c>); unknown icons stay plain-text.
    /// </summary>
    /// <param name="useNerdFont">When true, titles use Nerd Font glyphs; otherwise ASCII fallbacks.</param>
    public static IReadOnlyList<CommandItem> GetBuiltinCatalog(bool useNerdFont = false)
    {
        var list = new List<CommandItem>(BuiltinDefs.Length);
        foreach (var def in BuiltinDefs)
        {
            list.Add(MakeBuiltinItem(def, useNerdFont));
        }

        return list;
    }

    /// <summary>
    /// Combined default catalog: slash (10) + builtin (10), in that order.
    /// Empty query lists all 20 via the unchanged fuzzy path.
    /// </summary>
    /// <param name="useNerdFont">Glyph set for the builtin half.</param>
    public static IReadOnlyList<CommandItem> GetDefaultCatalog(bool useNerdFont = false)
    {
        var slash = SlashCatalog;
        var builtin = GetBuiltinCatalog(useNerdFont);
        var all = new List<CommandItem>(slash.Count + builtin.Count);
        all.AddRange(slash);
        all.AddRange(builtin);
        return all;
    }

    /// <summary>
    /// Exact lookup mirroring <c>SlashCommands.Find</c>: strips leading slashes,
    /// case-insensitive, alias-aware (<c>cls → /clear</c>, <c>exit → /quit</c>).
    /// </summary>
    /// <param name="command">User-typed command (e.g. <c>/help</c>, <c>help</c>, <c>cls</c>).</param>
    /// <returns>The matching slash item, or null.</returns>
    public static CommandItem? FindSlash(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = command.Trim().TrimStart('/');
        for (int i = 0; i < SlashDefs.Length; i++)
        {
            var def = SlashDefs[i];
            if (def.Name.TrimStart('/').Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return SlashCatalog[i];
            }

            foreach (string alias in def.Aliases)
            {
                if (alias.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return SlashCatalog[i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Exact builtin lookup by id slug (<c>open-session</c>) or pure title
    /// (<c>Open Session</c>), case-insensitive. The glyph prefix is not part of the query.
    /// </summary>
    public static CommandItem? FindBuiltin(string? query, bool useNerdFont = false)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        string trimmed = query.Trim();
        for (int i = 0; i < BuiltinDefs.Length; i++)
        {
            var def = BuiltinDefs[i];
            if (def.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                || def.Title.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return MakeBuiltinItem(def, useNerdFont);
            }
        }

        return null;
    }

    /// <summary>
    /// Combined exact lookup: slash first (slash/alias rules), then builtin (id/title).
    /// Fuzzy filtering itself stays inside <see cref="CommandPaletteView" /> via <see cref="FuzzyMatcher" />.
    /// </summary>
    public static CommandItem? Find(string? query, bool useNerdFont = false)
    {
        return FindSlash(query) ?? FindBuiltin(query, useNerdFont);
    }

    private static IReadOnlyList<CommandItem> BuildSlashCatalog()
    {
        var list = new List<CommandItem>(SlashDefs.Length);
        foreach (var def in SlashDefs)
        {
            string id = def.Name.TrimStart('/');
            string detail = def.Aliases.Length == 0
                ? def.Description
                : $"{def.Description} (alias: {string.Join(", ", def.Aliases)})";
            list.Add(new CommandItem(id, def.Name, detail, string.Empty, "Slash"));
        }

        return list;
    }

    private static CommandItem MakeBuiltinItem(BuiltinDef def, bool useNerdFont)
    {
        string glyph = PaletteIconMap.Resolve(def.IconKey, useNerdFont);
        string title = glyph.Length == 0 ? def.Title : $"{glyph} {def.Title}";
        return new CommandItem(def.Id, title, def.Subtitle, string.Empty, "Commands");
    }
}

