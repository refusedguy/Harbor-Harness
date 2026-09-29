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
