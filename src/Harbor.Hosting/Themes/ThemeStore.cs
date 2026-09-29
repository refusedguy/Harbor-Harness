using Harbor.DesignSystem;

namespace Harbor.Hosting.Themes;

/// <summary>Where a marketplace theme entry came from.</summary>
public enum ThemeSource
{
    /// <summary>Shipped with Harbor (<see cref="HarborTheme.BuiltIn" />).</summary>
    Builtin,

    /// <summary>Loaded from the user's themes directory.</summary>
    User,
}

/// <summary>
/// One entry in the theme marketplace: a built-in or a file from the themes
/// directory. Invalid files stay listed with <see cref="Errors" /> filled —
/// a broken theme never crashes the scan and never hides the other entries.
/// </summary>
public sealed record ThemeEntry(
    string FileName,
    string Name,
    ThemeSource Source,
    string? FilePath,
    HarborTheme? Theme,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when the entry carries a usable <see cref="Theme" />.</summary>
    public bool IsValid => Theme is not null;
}

/// <summary>
/// Theme marketplace store: resolves the themes directory
/// (<c>~/.harbor/themes</c>, overridable via <c>HARBOR_THEMES_DIR</c> or an
/// explicit path for tests), scans built-in + user themes, seeds the built-ins
/// as editable JSON files, and resolves by name with user override winning.
/// All file I/O is defensive — unreadable or malformed entries surface as
/// error entries instead of exceptions.
/// </summary>
/// <remarks>
///     <para>
///         The one production implementation of <see cref="IThemeStore" /> (#668).
///         The single-implementer count is enforced by ThemeStoreSeamRules rather
///         than left to review: a second implementer would be a second
///         read-and-parse, which is the duplication the issue exists to remove, and
///         it would look perfectly correct on its own. Note that the count is over
///         implementations of the PORT, not over every read of a theme file —
///         <see cref="ThemeDirectoryWatcher" /> still reads beside its owner, and
///         <c>IThemeStore</c>'s own remarks say so rather than leaving the next
///         reader to believe the port has no remaining callers to convert.
///     </para>
///     <para>
///         <b>#536 — why this type is not in Harbor.DesignSystem any more.</b> It
///         was, and it was the only thing in that assembly that touched a disk:
///         fourteen <c>File.*</c>/<c>Directory.*</c> calls and two reads of the
///         user's home directory, held green by four rows in
///         <c>PresentationCapabilityRules.KnownViolations</c>. Those rows were a
///         permission, not a fix. A design-system package is the one assembly a
///         consumer can take without pulling Harbor in — <c>IsPackable</c>,
///         <c>PackageId: Harbor.DesignSystem</c>, an empty allowed-reference set,
///         no PackageReference at all — so "where the user's themes live" cannot be
///         something it knows.
///     </para>
///     <para>
///         The port stayed behind on purpose, in the leaf, and this is the
///         implementation of a contract the leaf declares. Composition root
///         reaches Presentation, which Infrastructure in this repository may not
///         (<c>FullLayerMatrixTests</c>: an Infrastructure row may reference Domain,
///         Application, or a same-family Infrastructure sibling — never
///         Presentation), so "the persistence half belongs in Infrastructure", as
///         #536's own fix direction says, is not a destination this matrix has.
///         Harbor.Hosting is: it is the layer above Infrastructure, it is
///         unrestricted by rule, and the desktop app already references it, so the
///         theme marketplace stayed reachable by both products instead of becoming
///         a CLI internal. The tokens, <c>ThemeJson</c>,
///         <c>TerminalColorPalette</c>, <c>ThemeParseResult</c> and the port itself
///         are pure and stayed in the leaf; this type is the persistence half, and
///         persistence is an outer layer's job.
///     </para>
/// </remarks>
public sealed class ThemeStore : IThemeStore
{
    private readonly string _directory;

    /// <summary>Creates a store over <paramref name="directory" />; null resolves the default.</summary>
    public ThemeStore(string? directory = null) => _directory = directory ?? DefaultDirectory();

    /// <summary>The directory this store operates on.</summary>
    public string ThemesDirectory => _directory;

    /// <summary>
    /// Default marketplace directory: <c>$HARBOR_THEMES_DIR</c> when set, else
    /// <c>~/.harbor/themes</c>.
    /// </summary>
    public static string DefaultDirectory()
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_THEMES_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harbor", "themes");
    }

    /// <summary>Creates the themes directory if missing (idempotent).</summary>
    public void EnsureDirectory()
    {
        if (!Directory.Exists(_directory))
        {
            Directory.CreateDirectory(_directory);
        }
    }

    /// <summary>
    /// Full marketplace listing: built-ins first (switcher order), then user
    /// themes sorted by file name. Invalid user files appear as error entries.
    /// </summary>
    public IReadOnlyList<ThemeEntry> Scan()
    {
        var entries = new List<ThemeEntry>();
        foreach (var theme in HarborTheme.BuiltIn)
        {
            entries.Add(new ThemeEntry(
                FileName: "<builtin>",
                Name: theme.Name,
                Source: ThemeSource.Builtin,
                FilePath: null,
                Theme: theme,
                Errors: [],
                Warnings: []));
        }

        entries.AddRange(ScanUserFiles());
        return entries;
    }

    /// <summary>User themes only — valid entries from the themes directory.</summary>
    public IReadOnlyList<HarborTheme> LoadUserThemes() =>
        ScanUserFiles()
            .Where(e => e.IsValid)
            .Select(e => e.Theme!)
            .ToList();

    /// <summary>
    /// Writes the built-in themes as JSON files into the themes directory
    /// (skip-if-exists, idempotent) so users have editable starting points.
    /// Returns the paths written.
    /// </summary>
    public IReadOnlyList<string> SeedBuiltIns()
    {
        EnsureDirectory();
        var written = new List<string>();
        foreach (var theme in HarborTheme.BuiltIn)
        {
            string path = Path.Combine(_directory, theme.Name + ".json");
            if (File.Exists(path))
            {
                continue;
            }

            File.WriteAllText(path, ThemeJson.Write(theme));
            written.Add(path);
        }

        return written;
    }

    /// <summary>
    /// Resolves a theme by name (case-insensitive). A user theme with the same
    /// name wins over the built-in; unknown names return null.
    /// </summary>
    public HarborTheme? Resolve(string name)
    {
        foreach (var entry in ScanUserFiles())
        {
            if (entry.IsValid && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Theme;
            }
        }

        return HarborTheme.BuiltIn.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ThemeEntry> ScanUserFiles()
    {
        string[] files;
        try
        {
            files = Directory.Exists(_directory)
                ? Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception ex)
        {
            return [BrokenEntry("<themes directory>", [ex.Message])];
        }

        var entries = new List<ThemeEntry>(files.Length);
        foreach (string path in files)
        {
            entries.Add(LoadEntry(path));
        }

        return entries;
    }

    private ThemeEntry LoadEntry(string path)
    {
        // Read-and-parse goes through the port member, not a second copy of it:
        // this was a third answer to one question, and the store is the one that
        // is supposed to be the answer.
        ThemeParseResult result = LoadFile(path);
        return new ThemeEntry(
            FileName: Path.GetFileName(path),
            Name: result.IsSuccess ? result.Theme.Name : Path.GetFileNameWithoutExtension(path),
            Source: ThemeSource.User,
            FilePath: path,
            Theme: result.IsSuccess ? result.Theme : null,
            Errors: result.Errors,
            Warnings: result.Warnings);
    }

    /// <inheritdoc />
    public ThemeParseResult LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ThemeParseResult.Fail([$"theme file not found: {path}"], []);
            }

            return ThemeJson.Parse(File.ReadAllText(path), TerminalColorPalette.Current);
        }
        catch (Exception ex)
        {
            return ThemeParseResult.Fail([$"theme load failed: {ex.Message}"], []);
        }
    }

    /// <inheritdoc />
    public bool TryGetLastWriteUtc(string path, out DateTime lastWriteUtc)
    {
        try
        {
            if (!File.Exists(path))
            {
                lastWriteUtc = default;
                return false;
            }

            lastWriteUtc = File.GetLastWriteTimeUtc(path);
            return true;
        }
        catch (Exception)
        {
            // A file that cannot be stat'ed is a file a poller has nothing to do
            // about. Reporting it as a change would hand the caller a timestamp
            // it could not read, and re-reading it would fail the same way.
            lastWriteUtc = default;
            return false;
        }
    }

    private static ThemeEntry BrokenEntry(string fileName, IReadOnlyList<string> errors) => new(
        FileName: fileName,
        Name: fileName,
        Source: ThemeSource.User,
        FilePath: null,
        Theme: null,
        Errors: errors,
        Warnings: []);
}
