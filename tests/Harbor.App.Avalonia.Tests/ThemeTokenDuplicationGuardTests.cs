using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #673 guard: the HDS theme dictionaries are the ONE source of truth for
///     the Avalonia app's design tokens, so a hex value declared in
///     <c>Themes/**.axaml</c> must never be typed again in the app's C#.
/// </summary>
/// <remarks>
///     <para>
///         The duplication this blocks is not stylistic.
///         <c>ThemeSettingsViewModel</c> used to carry a hand-copied table of
///         palette previews — the same <c>MochaBase</c> / <c>AccentColor</c> /
///         <c>MochaText</c> values that <c>CatppuccinMocha.axaml</c> declares,
///         re-parsed as <c>Color.Parse</c> literals. Nothing tied the two
///         together, so re-tuning a palette desynchronised the Settings
///         thumbnail from the theme it previews, and only a human looking at a
///         running app could see it: no test read the palettes, and
///         <see cref="ThemeParityTests" /> compares <em>key sets</em> across theme
///         files — it cannot see a value that lives in C#.
///     </para>
///     <para>
///         This makes rule 9 of <c>AGENTS.md</c> ("the XAML
///         <c>ResourceDictionary</c> is the source of truth for design tokens")
///         mechanical instead of a review convention.
///     </para>
///     <para>
///         SCOPE — the Avalonia app only (<c>apps/Harbor.App.Avalonia</c>), and
///         that is deliberate. Some of the values it declares also appear in
///         <c>src/Harbor.DesignSystem</c> and in the TUI's JSON themes, but that
///         is a different, frozen theme axis with its own source of truth
///         (<c>JsonThemeLoader</c> and the JSON palettes) — a coincidence of
///         palette values, not a copy of these dictionaries. <c>contrib/</c> is
///         not built by CI and is not scanned.
///     </para>
///     <para>
///         <b>#755 — the same rule for ICON GEOMETRY.</b> The colour guard was
///         blind to the other half of the same design system.
///         <c>Themes/Hds/Icons.axaml</c> declares every <c>Ic*</c> glyph as
///         <c>StreamGeometry</c> path data, and the app's C# re-typed that data
///         and <c>Geometry.Parse</c>'d it per call: six values byte-identical to a
///         declared <c>Ic*</c> key, plus two near-copies of <c>IcInfo</c> whose
///         subpaths had already drifted from the dictionary. That is the failure
///         this file exists to prevent, in the one place it could not see.
///     </para>
/// </remarks>
public class ThemeTokenDuplicationGuardTests
{
    /// <summary>
    ///     This file names theme colours in its documentation, so it opts itself
    ///     out — a guard that fails on its own explanatory prose is noise.
    /// </summary>
    private static string GuardFileName => $"{typeof(ThemeTokenDuplicationGuardTests).Name}.cs";

    private static string AppDir => Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia");

    private static string ThemesDir => Path.Combine(AppDir, "Themes");

    /// <summary>
    ///     The 8-digit form comes first so an <c>#AARRGGBB</c> literal is not
    ///     truncated to its leading 6 digits, which would match a different
    ///     token than the one that was actually written.
    /// </summary>
    private static readonly Regex HexColor = new(
        @"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Test]
    public async Task No_Hds_Theme_Color_Appears_In_Avalonia_App_CSharp()
    {
        var declared = ThemeColorValues();
        await Assert.That(declared.Count).IsGreaterThan(0);

        List<string> violations = ColorLiteralsInAppCSharp()
            .Where(site => declared.Contains(site.Value.ToUpperInvariant()))
            .Select(Describe)
            .ToList();

        foreach (string violation in violations)
            Console.WriteLine(violation);

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     The ratchet form of the same rule: once the palette previews were read
    ///     from the dictionaries, the app's C# holds no colour literal at all.
    ///     Avalonia has a brush for every role a control needs, so a literal in
    ///     C# can only be a second source of truth waiting to drift.
    /// </summary>
    [Test]
    public async Task Avalonia_App_CSharp_Contains_No_Color_Literal_At_All()
    {
        List<string> violations = ColorLiteralsInAppCSharp().Select(Describe).ToList();

        foreach (string violation in violations)
            Console.WriteLine(violation);

        await Assert.That(violations).IsEmpty();
    }

    private static string Describe(HexSite site) =>
        $"{Path.GetRelativePath(FindRepoRoot(), site.File).Replace('\\', '/')}:{site.Line} " +
        $"'{site.Value}' is declared in Themes/*.axaml — resolve it through the " +
        "ResourceDictionary instead of typing a colour here.";

    private sealed record HexSite(string File, int Line, string Value);

    /// <summary>
    ///     #755 — the geometry half of the rule above. A <c>StreamGeometry</c>
    ///     value declared in <c>Themes/Hds/Icons.axaml</c> must not be re-typed
    ///     into the app's C#: the C# re-declares a design-system value, and
    ///     nothing ties the two together afterwards.
    /// </summary>
    /// <remarks>
    ///     SCOPE — the same tree as the colour guard above (<c>apps/</c>, not
    ///     <c>contrib/</c>, not <c>bin/</c>/<c>obj/</c>), read from the same
    ///     <c>Themes/</c> directory, and for the same reason: <c>docs/ui/HDS.md</c>
    ///     names <c>Icons.axaml</c> as a cascade layer of the same design
    ///     system, so a value in it is a token like any other.
    /// </remarks>
    [Test]
    public async Task No_Hds_Icon_Path_Appears_In_Avalonia_App_CSharp()
    {
        var declared = DeclaredIconPaths();
        await Assert.That(declared.Count).IsGreaterThan(0);

        List<string> violations = IconPathLiteralsInAppCSharp()
            .Where(site => declared.Any(d => site.Value.Contains(d.Data, StringComparison.Ordinal)))
            .Select(site => DescribeIcon(site, declared.First(d => site.Value.Contains(d.Data, StringComparison.Ordinal)).Key))
            .ToList();

        foreach (string violation in violations)
            Console.WriteLine(violation);

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     The ratchet form of the same rule, mirroring
    ///     <see cref="Avalonia_App_CSharp_Contains_No_Color_Literal_At_All" />:
    ///     once the converters resolve their glyphs through the dictionary, the
    ///     app's C# holds no SVG path data at all, so no future literal can be a
    ///     copy of one.
    /// </summary>
    /// <remarks>
    ///     This is strictly stronger than the exact-match test above, and it is
    ///     the one that catches the case that had ALREADY gone wrong: two of the
    ///     literals were near-copies of <c>IcInfo</c> whose subpaths had drifted,
    ///     so no byte comparison would ever have flagged them.
    /// </remarks>
    [Test]
    public async Task Avalonia_App_CSharp_Contains_No_Icon_Path_Literal_At_All()
    {
        List<string> violations = IconPathLiteralsInAppCSharp().Select(DescribeIcon).ToList();

        foreach (string violation in violations)
            Console.WriteLine(violation);

        await Assert.That(violations).IsEmpty();
    }

    private static string DescribeIcon(IconPathSite site) =>
        $"{Path.GetRelativePath(FindRepoRoot(), site.File).Replace('\\', '/')}:{site.Line} " +
        $"'{Shorten(site.Value)}' is SVG path data — resolve an 'Ic*' key through " +
        "Themes/Hds/Icons.axaml instead of carrying a second copy of a glyph.";

    private static string DescribeIcon(IconPathSite site, string key) =>
        $"{Path.GetRelativePath(FindRepoRoot(), site.File).Replace('\\', '/')}:{site.Line} " +
        $"'{Shorten(site.Value)}' is a verbatim copy of '{key}' from Themes/Hds/Icons.axaml — " +
        "resolve the key through the ResourceDictionary instead.";

    private static string Shorten(string value) =>
        value.Length <= 24 ? value : value[..24] + "…";

    private sealed record IconPathSite(string File, int Line, string Value);

    private sealed record DeclaredIconPath(string Key, string Data);

    /// <summary>
    ///     A quoted string that opens like SVG path data: an absolute moveto
    ///     followed by a long run of coordinates. The 20-character floor is what
    ///     keeps it off short strings that merely start with a capital M and a
    ///     digit — a log format, a message.
    /// </summary>
    private static readonly Regex SvgPathLiteral = new(
        @"""M[0-9][^""]{20,}""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The <c>&lt;StreamGeometry x:Key="Ic…"&gt;path&lt;/StreamGeometry&gt;</c>
    ///     entries of the icon dictionary, read from the XML as text so the guard
    ///     needs no Avalonia <c>Application</c> — the same reason
    ///     <c>IconTests</c> validates the file as XML.
    /// </summary>
    private static readonly Regex DeclaredIconPathEntry = new(
        @"x:Key=""(?<key>Ic[A-Za-z0-9]+)""\s*>(?<path>M[^<]+)<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<DeclaredIconPath> DeclaredIconPaths() =>
        ThemeFiles()
            .SelectMany(file => DeclaredIconPathEntry.Matches(File.ReadAllText(file))
                .Select(m => new DeclaredIconPath(m.Groups["key"].Value, m.Groups["path"].Value)))
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<IconPathSite> IconPathLiteralsInAppCSharp() =>
        AppCSharpFiles()
            .SelectMany(file => File.ReadAllLines(file)
                .Select((text, index) => (text, line: index + 1))
                .SelectMany(entry => SvgPathLiteral.Matches(entry.text)
                    .Select(match => new IconPathSite(file, entry.line, match.Value))))
            .ToArray();

    private static IReadOnlyList<HexSite> ColorLiteralsInAppCSharp() =>
        AppCSharpFiles()
            .SelectMany(file => File.ReadAllLines(file)
                .Select((text, index) => (text, line: index + 1))
                .SelectMany(entry => HexColor.Matches(entry.text)
                    .Select(match => new HexSite(file, entry.line, match.Value))))
            .ToArray();

    /// <summary>Every colour literal the app's theme dictionaries declare.</summary>
    private static HashSet<string> ThemeColorValues() =>
        ThemeFiles()
            .SelectMany(file => HexColor.Matches(File.ReadAllText(file)).Select(m => m.Value.ToUpperInvariant()))
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> ThemeFiles() =>
        Directory.Exists(ThemesDir)
            ? Directory.EnumerateFiles(ThemesDir, "*.axaml", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static IReadOnlyList<string> AppCSharpFiles() =>
        Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                               && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                               && !string.Equals(Path.GetFileName(file), GuardFileName, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Harbor.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}
