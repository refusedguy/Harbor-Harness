using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Commands;
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
/// CF-E-017: catalog feeding the CellForge command palette.
/// <para>
/// The slash half is projected from <c>Harbor.Ui.Framework.Commands.SlashCommandCatalog</c>
/// (#462) — the same registry the CLI slash dispatcher binds handlers to. It used to be a
/// 15-entry literal that had drifted: the palette advertised <c>/tokens</c>, <c>/theme</c>,
/// <c>/editor</c>, <c>/diff</c> and <c>/branch</c>, none of which the dispatcher can run, so
/// picking them produced "Unknown command", while omitting <c>/new</c>, <c>/permissions</c>,
/// <c>/plugins</c>, <c>/skills</c>, <c>/tree</c>, <c>/fork</c> and <c>/renderer</c>, which it can.
/// </para>
/// <para>
/// The builtin half keeps its literal copy of <c>BuiltInCommands.Templates()</c>: those are
/// palette <i>actions</i> (overlay ids, not slash text), so they are a different vocabulary and
/// are not part of the slash registry. No project reference to <c>Harbor.Desktop.Shared</c>
/// is taken on purpose — the architecture matrix forbids a
/// <c>Harbor.Tui.CellForge → Harbor.Desktop.Shared</c> edge. Existing palette behavior
/// (fuzzy via <see cref="FuzzyMatcher" />, groups, navigation,
/// <see cref="CommandPaletteView.OnCommit" />) is untouched — only item sources change.
/// </para>
/// </summary>
public static class CommandPaletteCatalog
{
    private sealed record SlashDef(string Name, string Description, IReadOnlyList<string> Aliases);

    private sealed record BuiltinDef(string Title, string Subtitle, string IconKey, string Id);

    /// <summary>Slash half, projected from the single registry (#462).</summary>
    private static readonly SlashDef[] SlashDefs = BuildSlashDefs();

    private static readonly CommandItem[] SlashItems = BuildSlashItems();

    private static readonly Dictionary<string, CommandItem> SlashByName = BuildSlashLookup();

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

    private static SlashDef[] BuildSlashDefs()
    {
        var defs = new SlashDef[SlashCommandCatalog.All.Count];
        int i = 0;
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            defs[i++] = new SlashDef(def.Invocation, def.Description, def.Aliases);
        }

        return defs;
    }

    /// <summary>Slash catalog (group "Slash"). Derived from <c>SlashCommandCatalog</c>.</summary>
    public static IReadOnlyList<CommandItem> SlashCatalog { get; } = SlashItems;

    /// <summary>Builds the slash catalog (group "Slash").</summary>
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
    /// Combined default catalog: slash (from <c>SlashCommandCatalog</c>) + builtin, in that order.
    /// Empty query lists them all via the unchanged fuzzy path.
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
    /// Exact slash lookup, mirroring <c>SlashCommands.Find</c>: strips leading slashes,
    /// case-insensitive, alias-aware (<c>quit → /exit</c>, <c>h → /help</c>).
    /// </summary>
    /// <param name="command">User-typed command (e.g. <c>/help</c>, <c>help</c>, <c>cls</c>).</param>
    /// <returns>The matching slash item, or null.</returns>
    public static CommandItem? FindSlash(string? command)
    {
        SlashCommandDefinition? def = SlashCommandCatalog.Find(command);
        return def is null ? null : SlashByName[def.Name];
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

    private static CommandItem[] BuildSlashItems()
    {
        var items = new CommandItem[SlashDefs.Length];
        for (int i = 0; i < SlashDefs.Length; i++)
        {
            SlashDef def = SlashDefs[i];
            string id = def.Name.TrimStart('/');
            string detail = def.Aliases.Count == 0
                ? def.Description
                : $"{def.Description} (alias: {string.Join(", ", def.Aliases)})";
            items[i] = new CommandItem(id, def.Name, detail, string.Empty, "Slash");
        }

        return items;
    }

    private static Dictionary<string, CommandItem> BuildSlashLookup()
    {
        var lookup = new Dictionary<string, CommandItem>(SlashDefs.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < SlashDefs.Length; i++)
        {
            lookup[SlashDefs[i].Name] = SlashItems[i];
        }

        return lookup;
    }

    private static CommandItem MakeBuiltinItem(BuiltinDef def, bool useNerdFont)
    {
        string glyph = PaletteIconMap.Resolve(def.IconKey, useNerdFont);
        string title = glyph.Length == 0 ? def.Title : $"{glyph} {def.Title}";
        return new CommandItem(def.Id, title, def.Subtitle, string.Empty, "Commands");
    }
}

