using System.Xml;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Verifies that every HDS theme file in Themes/Hds/ defines the same set
///     of resource keys as the baseline (CatppuccinMocha). A theme that is
///     missing a key will cause a runtime KeyNotFoundException when an HDS
///     component style tries to resolve a brush it doesn't provide.
/// </summary>
/// <remarks>
///     <para>
///         This is a pure-XML-parity test — it does NOT require an Avalonia
///         Application to be running. It parses each .axaml file as XML and
///         extracts every <c>x:Key</c> attribute, then asserts the key sets
///         are identical. This catches the exact regression that happened to
///         HdsButton.axaml: a theme defines <c>AccentPrimaryBrush</c> but
///         forgets <c>TextPrimaryBrush</c>, so the button renders with missing
///         foreground on that theme.
///     </para>
/// </remarks>
public class ThemeParityTests
{
    private static readonly string HdsThemesDir = Path.Combine(
        FindRepoRoot(), "apps", "Harbor.App.Avalonia", "Themes", "Hds");

    /// <summary>Shared structural base every HDS theme layers its palette on top of.</summary>
    private const string BaseDictionary = "BaseTokens.axaml";

    /// <summary>
    ///     Every HDS theme file, discovered from the directory (#581) — never a
    ///     hand-listed set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The list used to be a literal array of six names, and it had already
    ///         drifted: it is a hand-maintained copy of "which files in
    ///         <c>Themes/Hds/</c> are themes", and nothing tied the two together. A theme
    ///         file added without an array edit would never be parity-checked.
    ///     </para>
    ///     <para>
    ///         The predicate is the design system's own definition, stated in
    ///         <c>BaseTokens.axaml</c>: structural tokens live there, and
    ///         <em>"colors and brushes … live in the theme files that merge this
    ///         base"</em>. So a theme is a dictionary that merges
    ///         <see cref="BaseDictionary" />; the structural dictionaries
    ///         (<c>BaseTokens</c> itself, <c>Elevation</c> which it merges,
    ///         <c>Typography</c> which is a <c>&lt;Styles/&gt;</c> root, and
    ///         <c>Icons</c> which is geometry) do not and are correctly excluded. The
    ///         set is derived per file, so adding a theme is one declaration: drop the
    ///         .axaml in the folder and it is covered.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<string> ThemeFiles =>
        Directory.EnumerateFiles(HdsThemesDir, "*.axaml")
            .Where(IsPalette)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     Whether <paramref name="path" /> is a palette dictionary: a
    ///     <c>&lt;ResourceDictionary&gt;</c> that merges the shared structural base.
    /// </summary>
    private static bool IsPalette(string path)
    {
        var doc = XDocument.Load(path);
        if (doc.Root?.Name.LocalName != "ResourceDictionary")
        {
            return false; // Typography.axaml is a <Styles/> root — classes, not tokens.
        }

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceInclude")
            .Select(e => (string?)e.Attribute("Source") ?? string.Empty)
            .Any(src => src.EndsWith(BaseDictionary, StringComparison.Ordinal));
    }

    [Test]
    public async Task All_Hds_Themes_Have_Same_Key_Set_As_CatppuccinMocha()
    {
        var baseline = ExtractKeys(Path.Combine(HdsThemesDir, "CatppuccinMocha.axaml"));
        await Assert.That(baseline.Count > 0).IsTrue();

        // Non-trivial by construction: a single discovered file would make the
        // comparison below vacuously true.
        IReadOnlyList<string> themes = ThemeFiles;
        await Assert.That(themes.Count).IsGreaterThan(1);

        foreach (var path in themes)
        {
            var keys = ExtractKeys(path);
            var missing = baseline.Except(keys).ToArray();
            var extra = keys.Except(baseline).ToArray();

            await Assert.That(missing.Length).IsEqualTo(0);
            await Assert.That(extra.Length).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     The parity baseline itself is one of the discovered themes, so the suite
    ///     cannot pass by classifying every file out.
    /// </summary>
    [Test]
    public async Task Baseline_IsPartOfTheDiscoveredThemeSet()
    {
        await Assert.That(ThemeFiles.Any(t =>
            string.Equals(Path.GetFileName(t), "CatppuccinMocha.axaml", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task BaseTokens_Axaml_Defines_Expected_Structural_Tokens()
    {
        var path = Path.Combine(HdsThemesDir, "BaseTokens.axaml");
        var keys = ExtractKeys(path);

        string[] expected = {
            "Space1", "Space6", "Space12",
            "RadiusXs", "RadiusSm", "RadiusMd", "RadiusLg", "RadiusXl",
            "RadiusFull", "RadiusNone",
            "MotionInstant", "MotionFast", "MotionBase", "MotionSlow", "MotionNormal", "MotionFaster",
            "EaseStandard", "EaseFast", "EaseNormal", "TransitionBrush",
            "FontSizeCaption", "FontSizeBody", "FontSizeHeading",
            "FontWeightNormal", "FontWeightSemiBold", "FontWeightBold"
        };

        foreach (var key in expected)
        {
            await Assert.That(keys.Contains(key)).IsTrue();
        }
    }

    [Test]
    public async Task Elevation_Axaml_Defines_Shadow_Tokens()
    {
        var path = Path.Combine(HdsThemesDir, "Elevation.axaml");
        var keys = ExtractKeys(path);

        string[] expected = {
            "ShadowNone", "ShadowSm", "ShadowMd", "ShadowLg", "ShadowXl"
        };

        foreach (var key in expected)
        {
            await Assert.That(keys.Contains(key)).IsTrue();
        }
    }

    private static HashSet<string> ExtractKeys(string path)
    {
        var doc = XDocument.Load(path);
        var xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        var keys = new HashSet<string>();
        foreach (var elem in doc.Descendants()
            .Where(e => e.Attribute(XName.Get("Key", xNs)) is not null))
        {
            var key = elem.Attribute(XName.Get("Key", xNs))?.Value;
            if (!string.IsNullOrEmpty(key))
                keys.Add(key);
        }
        return keys;
    }

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
