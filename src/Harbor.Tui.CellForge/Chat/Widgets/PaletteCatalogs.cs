using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

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
        new("/new", "Create a new session", ["new-session"]),
        new("/model", "Switch the active LLM model", []),
        new("/agent", "Switch the active agent", []),
        new("/config", "Open the configuration editor", []),
        new("/setup", "Run the setup wizard", []),
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

