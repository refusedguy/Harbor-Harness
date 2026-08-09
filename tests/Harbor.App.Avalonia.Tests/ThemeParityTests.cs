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

    private static readonly string[] ThemeFiles =
    {
        "CatppuccinMocha.axaml",
        "Vapor.axaml",
        "Mono.axaml",
        "Paper.axaml",
        "Lumen.axaml"
    };

    [Test]
    public async Task All_Hds_Themes_Have_Same_Key_Set_As_CatppuccinMocha()
    {
        var baseline = ExtractKeys(Path.Combine(HdsThemesDir, "CatppuccinMocha.axaml"));
        await Assert.That(baseline.Count > 0).IsTrue();

        foreach (var theme in ThemeFiles)
        {
            var path = Path.Combine(HdsThemesDir, theme);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Theme file not found: {path}");
            }

            var keys = ExtractKeys(path);
            var missing = baseline.Except(keys).ToArray();
            var extra = keys.Except(baseline).ToArray();

            await Assert.That(missing.Length).IsEqualTo(0);
            await Assert.That(extra.Length).IsEqualTo(0);
        }
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
