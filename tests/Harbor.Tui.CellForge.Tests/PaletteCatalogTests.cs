using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-E-017 (TOP-1 #27): the palette is fed by cell-local mirrors of
/// <c>SlashCommands.All</c> (10) and <c>BuiltInCommands.Templates</c> (10).
/// Pins all 10+10 reachable via exact <c>Find</c> (slash strips "/" +
/// ignore-case + aliases), icon keys mapped (ASCII + Nerd Font, unknown →
/// plain-text without throw), and the pre-existing fuzzy/groups/navigation/
/// OnCommit behavior intact (only item sources added).
/// </summary>
public class PaletteCatalogTests
{
    private static readonly string[] ExpectedSlashTitles =
    [
        "/agent", "/branch", "/clear", "/config", "/diff",
        "/editor", "/help", "/model", "/new", "/providers",
        "/quit", "/sessions", "/setup", "/theme", "/tokens",
    ];

    private static readonly (string Title, string Id)[] ExpectedBuiltin =
    [
        ("Open Session", "sessionsFlyout"),
        ("New Session", "new-session"),
        ("Branch Session", "branch-session"),
        ("Toggle Theme", "toggle-theme"),
        ("Open Code Editor", "open-code-editor"),
        ("Open Diff View", "diff"),
        ("Open Token Usage", "tokenUsage"),
        ("Open Settings", "settings"),
        ("Open Provider Browser", "providerBrowser"),
        ("Quit", "quit"),
    ];

    private static readonly (string Key, string Ascii)[] ExpectedIcons =
    [
        ("FolderIcon", "e"),
        ("PlusIcon", "+"),
        ("BranchIcon", ">"),
        ("ThemeIcon", "~"),
        ("CodeIcon", "$"),
        ("DiffIcon", "?"),
        ("ChartIcon", "#"),
        ("SettingsIcon", "@"),
        ("ProviderIcon", "o"),
        ("QuitIcon", "*"),
    ];

    private static void Type(CommandPaletteView palette, string text)
    {
        foreach (char c in text)
        {
            _ = palette.HandleKey(KeyEvent.Char(new Rune(c)));
        }
    }

    [Test]
    public async Task SlashCatalog_HasTenMirroringDesktopSource()
    {
        var slash = CommandPaletteCatalog.SlashCatalog;

        await Assert.That(slash.Count).IsEqualTo(15);
        foreach (string title in ExpectedSlashTitles)
        {
            await Assert.That(slash.Any(i => i.Title == title)).IsTrue();
        }

        foreach (var item in slash)
        {
            await Assert.That(item.Group).IsEqualTo("Slash");
            await Assert.That(item.Id.StartsWith("/", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task BuiltinCatalog_HasTenMirroringDesktopTemplates()
    {
        var builtin = CommandPaletteCatalog.GetBuiltinCatalog();

        await Assert.That(builtin.Count).IsEqualTo(10);
        foreach (var (title, id) in ExpectedBuiltin)
        {
            await Assert.That(builtin.Any(i => i.Id == id)).IsTrue();
            await Assert.That(builtin.Any(i => i.Title.EndsWith(title, StringComparison.Ordinal))).IsTrue();
        }

        foreach (var item in builtin)
        {
            await Assert.That(item.Group).IsEqualTo("Commands");
        }
    }

    [Test]
    public async Task FindSlash_AllTen_BySlashName()
    {
        foreach (string title in ExpectedSlashTitles)
        {
            await Assert.That(CommandPaletteCatalog.FindSlash(title)).IsNotNull();
            await Assert.That(CommandPaletteCatalog.Find(title)).IsNotNull();
        }
    }

    [Test]
    public async Task FindSlash_StripsSlashAndIgnoresCase()
    {
        await Assert.That(CommandPaletteCatalog.FindSlash("/HELP")!.Id).IsEqualTo("help");
        await Assert.That(CommandPaletteCatalog.FindSlash("help")!.Id).IsEqualTo("help");
        await Assert.That(CommandPaletteCatalog.FindSlash("  /Clear  ")!.Id).IsEqualTo("clear");
        await Assert.That(CommandPaletteCatalog.FindSlash("SESSIONS")!.Id).IsEqualTo("sessions");
        await Assert.That(CommandPaletteCatalog.FindSlash("Theme")!.Id).IsEqualTo("theme");
    }

    [Test]
    public async Task FindSlash_Aliases()
    {
        await Assert.That(CommandPaletteCatalog.FindSlash("cls")!.Id).IsEqualTo("clear");
        await Assert.That(CommandPaletteCatalog.FindSlash("CLS")!.Id).IsEqualTo("clear");
        await Assert.That(CommandPaletteCatalog.FindSlash("/cls")!.Id).IsEqualTo("clear");
        await Assert.That(CommandPaletteCatalog.FindSlash("exit")!.Id).IsEqualTo("quit");
        await Assert.That(CommandPaletteCatalog.FindSlash("EXIT")!.Id).IsEqualTo("quit");
        await Assert.That(CommandPaletteCatalog.Find("cls")!.Title).IsEqualTo("/clear");
        await Assert.That(CommandPaletteCatalog.Find("exit")!.Title).IsEqualTo("/quit");
    }

    [Test]
    public async Task FindBuiltin_AllTen_ByTitleAndId()
    {
        foreach (var (title, id) in ExpectedBuiltin)
        {
            await Assert.That(CommandPaletteCatalog.FindBuiltin(title)).IsNotNull();
            await Assert.That(CommandPaletteCatalog.FindBuiltin(id)).IsNotNull();
            await Assert.That(CommandPaletteCatalog.FindBuiltin(title.ToUpperInvariant())).IsNotNull();
            await Assert.That(CommandPaletteCatalog.Find(title)).IsNotNull();
            await Assert.That(CommandPaletteCatalog.Find(id)).IsNotNull();
        }
    }

    [Test]
    public async Task Find_UnknownAndEmpty_ReturnsNull()
    {
        await Assert.That(CommandPaletteCatalog.Find("no-such-command")).IsNull();
        await Assert.That(CommandPaletteCatalog.FindSlash("nope")).IsNull();
        await Assert.That(CommandPaletteCatalog.FindBuiltin("nope")).IsNull();
        await Assert.That(CommandPaletteCatalog.Find(null)).IsNull();
        await Assert.That(CommandPaletteCatalog.Find(string.Empty)).IsNull();
        await Assert.That(CommandPaletteCatalog.Find("   ")).IsNull();
        await Assert.That(CommandPaletteCatalog.FindSlash(null)).IsNull();
        await Assert.That(CommandPaletteCatalog.FindBuiltin("")).IsNull();
    }

    [Test]
    public async Task Icons_Ascii_MapsAllTenKeys()
    {
        foreach (var (key, ascii) in ExpectedIcons)
        {
            await Assert.That(PaletteIconMap.ToAscii(key)).IsEqualTo(ascii);
            await Assert.That(PaletteIconMap.Resolve(key)).IsEqualTo(ascii);
        }
    }

    [Test]
    public async Task Icons_Nerd_MapsAllTenKeys_Distinct()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, _) in ExpectedIcons)
        {
            string nerd = PaletteIconMap.ToNerdFont(key);
            await Assert.That(nerd.Length > 0).IsTrue();
            await Assert.That(PaletteIconMap.Resolve(key, useNerdFont: true)).IsEqualTo(nerd);
            seen.Add(nerd);
        }

        await Assert.That(seen.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Icons_Unknown_PlainTextWithoutThrow()
    {
        await Assert.That(PaletteIconMap.Resolve(null)).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.Resolve(string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.Resolve("   ")).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.Resolve("NopeIcon")).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.Resolve("foldericon")).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.ToAscii("NopeIcon")).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.ToNerdFont("NopeIcon")).IsEqualTo(string.Empty);
        await Assert.That(PaletteIconMap.Resolve("NopeIcon", useNerdFont: true)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Fuzzy_Slash_StillWorks()
    {
        var palette = new CommandPaletteView();
        palette.Show(CommandPaletteCatalog.GetDefaultCatalog());
        Type(palette, "sess");

        await Assert.That(palette.Results.Any(r => r.Title == "/sessions")).IsTrue();

        var tokens = new CommandPaletteView();
        tokens.Show(CommandPaletteCatalog.GetDefaultCatalog());
        Type(tokens, "tok");

        await Assert.That(tokens.Results.Any(r => r.Title == "/tokens")).IsTrue();

        var alias = new CommandPaletteView();
        alias.Show(CommandPaletteCatalog.GetDefaultCatalog());
        Type(alias, "cls");

        await Assert.That(alias.Results.Any(r => r.Title == "/clear")).IsTrue();

        var quit = new CommandPaletteView();
        quit.Show(CommandPaletteCatalog.GetDefaultCatalog());
        Type(quit, "EXIT");

        await Assert.That(quit.Results.Any(r => r.Title == "/quit")).IsTrue();
    }

    [Test]
    public async Task Fuzzy_Builtin_StillWorks()
    {
        var palette = new CommandPaletteView();
        palette.Show(CommandPaletteCatalog.GetDefaultCatalog());
        Type(palette, "theme");

        await Assert.That(palette.Results.Any(r => r.Title.EndsWith("Toggle Theme", StringComparison.Ordinal))).IsTrue();
        await Assert.That(palette.Results.Any(r => r.Title == "/theme")).IsTrue();

        var providers = new CommandPaletteView();
        providers.Show(CommandPaletteCatalog.GetDefaultCatalog());
        Type(providers, "provider");

        await Assert.That(providers.Results.Any(r => r.Title.EndsWith("Open Provider Browser", StringComparison.Ordinal))).IsTrue();
        await Assert.That(providers.Results.Any(r => r.Title == "/providers")).IsTrue();
    }

    [Test]
    public async Task Palette_ShowDefaultCatalog_ListsTwenty_WithNavigationAndCommit()
    {
        var palette = new CommandPaletteView();
        palette.ShowDefaultCatalog();

        await Assert.That(palette.Visible).IsTrue();
        await Assert.That(palette.Results.Count).IsEqualTo(25);
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);

        _ = palette.HandleKey(KeyEvent.Simple(KeyCode.Down));
        await Assert.That(palette.SelectedIndex).IsEqualTo(1);

        _ = palette.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(palette.SelectedIndex).IsEqualTo(0);

        CommandItem? committed = null;
        palette.OnCommit = item => committed = item;
        _ = palette.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(committed).IsNotNull();
        await Assert.That(palette.Visible).IsFalse();
    }

    [Test]
    public async Task Palette_Paint_WithCatalog_ShowsItemsAndHints()
    {
        var palette = new CommandPaletteView();
        palette.ShowDefaultCatalog();

        var buffer = new ScreenBuffer(60, 24);
        palette.Paint(buffer, new Rect(2, 1, 56, 20));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("/agent");
        await Assert.That(art).Contains("New Session");
        await Assert.That(art).Contains("esc close");
    }
}
