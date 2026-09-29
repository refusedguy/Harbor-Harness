using System.Xml.Linq;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Harbor.App.Avalonia.Themes;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #673 — the palette index is the app's one answer to "which HDS palettes
///     exist, what colour is each one, and is each one dark". These tests pin
///     that answer to the palette dictionaries, so it cannot drift back into a
///     second source of truth.
/// </summary>
/// <remarks>
///     <para>
///         The XML half needs no Avalonia runtime: the folder is walked with the
///         same "a palette is a dictionary that merges BaseTokens.axaml"
///         definition <see cref="ThemeParityTests" /> uses, and the expected
///         colours are read straight out of the .axaml source. The headless half
///         proves the runtime read resolves those same values, which is the claim
///         a Settings thumbnail actually rests on.
///     </para>
/// </remarks>
[NotInParallel("avalonia-headless")]
public class HdsThemeCatalogParityTests
{
    private const string XNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string BaseDictionary = "BaseTokens.axaml";

    private static readonly string HdsThemesDir = Path.Combine(
        FindRepoRoot(), "apps", "Harbor.App.Avalonia", "Themes", "Hds");

    /// <summary>
    ///     The palette dictionaries, discovered from the folder the way
    ///     <see cref="ThemeParityTests" /> discovers them — a
    ///     <c>&lt;ResourceDictionary&gt;</c> that merges the shared structural
    ///     base. Hand-listing the file names here is the very drift this issue is
    ///     about, so the list is derived.
    /// </summary>
    private static IReadOnlyList<string> PaletteFiles =>
        Directory.EnumerateFiles(HdsThemesDir, "*.axaml")
            .Where(IsPalette)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool IsPalette(string path)
    {
        var doc = XDocument.Load(path);
        if (doc.Root?.Name.LocalName != "ResourceDictionary")
        {
            return false;
        }

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceInclude")
            .Select(e => (string?)e.Attribute("Source") ?? string.Empty)
            .Any(src => src.EndsWith(BaseDictionary, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Every palette file is offered in Settings. Before #673 the C# preview
    ///     table named five palettes while the folder holds six — the Settings
    ///     list had silently missed <c>HarborDesignTokens</c>, and the
    ///     hand-written "which palettes are dark" array had missed it too, so
    ///     applying that palette from anywhere would have flipped a dark theme to
    ///     the light variant.
    /// </summary>
    [Test]
    public async Task Catalog_Offers_Every_Palette_Dictionary_In_The_Folder()
    {
        List<string> declared =
        [
            .. PaletteFiles.Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty),
        ];
        IReadOnlyList<string> indexed = HdsThemeCatalog.PaletteNames;

        List<string> missing = [.. declared.Except(indexed, StringComparer.Ordinal)];
        List<string> unknown = [.. indexed.Except(declared, StringComparer.Ordinal)];

        await Assert.That(missing).IsEmpty();
        await Assert.That(unknown).IsEmpty();
        await Assert.That(indexed.Count).IsGreaterThan(1);
    }

    /// <summary>
    ///     Every palette declares the <see cref="ThemeVariant" /> it is designed
    ///     for, so no C# side has to know which palettes are dark. Parity
    ///     already forces the key to exist in all of them; this pins the value to
    ///     a real <see cref="ThemeVariant" /> name.
    /// </summary>
    [Test]
    public async Task Every_Palette_Declares_A_Real_ThemeVariant()
    {
        string dark = ThemeVariant.Dark.Key.ToString() ?? "Dark";
        string light = ThemeVariant.Light.Key.ToString() ?? "Light";

        List<string> offenders = [];
        foreach (var path in PaletteFiles)
        {
            string? declared = ReadValue(path, HdsThemeCatalog.VariantKey);
            if (!string.Equals(declared, dark, StringComparison.Ordinal)
                && !string.Equals(declared, light, StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(path)}: {HdsThemeCatalog.VariantKey}='{declared ?? "<missing>"}'");
            }
        }

        foreach (string offender in offenders)
            Console.WriteLine(offender);

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     The three brushes a thumbnail draws with resolve at runtime to what
    ///     the .axaml file says, and the palette's dark/light answer travels with
    ///     the palette. A preview that shows anything other than the palette it
    ///     previews is the defect, so the expectation is the file's own value.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task Catalog_Resolves_Preview_Brushes_From_The_Palette_Dictionaries()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            foreach (string name in HdsThemeCatalog.PaletteNames)
            {
                HdsThemePreview? preview = HdsThemeCatalog.Find(name);
                await Assert.That(preview).IsNotNull();

                string path = Path.Combine(HdsThemesDir, name + ".axaml");

                await Assert.That(ColorOf(preview!.Surface))
                    .IsEqualTo(DeclaredColor(path, "AppBackgroundBrush"));
                await Assert.That(ColorOf(preview.Accent))
                    .IsEqualTo(DeclaredColor(path, "AccentBrush"));
                await Assert.That(ColorOf(preview.Text))
                    .IsEqualTo(DeclaredColor(path, "TextBrush"));

                string declared = ReadValue(path, HdsThemeCatalog.VariantKey) ?? string.Empty;
                bool expectDark = string.Equals(
                    declared, ThemeVariant.Dark.Key.ToString() ?? "Dark", StringComparison.Ordinal);

                await Assert.That(preview.IsDark).IsEqualTo(expectDark);
                await Assert.That(preview.Variant)
                    .IsEqualTo(expectDark ? ThemeVariant.Dark : ThemeVariant.Light);
            }
        }, CancellationToken.None);
    }

    /// <summary>Every declared palette loads — none is silently dropped.</summary>
    [Test]
    [Retry(3)]
    public async Task Catalog_Loads_One_Preview_Per_Declared_Palette()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            await Assert.That(HdsThemeCatalog.Previews.Count).IsEqualTo(HdsThemeCatalog.PaletteNames.Count);
            await Assert.That(HdsThemeCatalog.Find("Vapor")).IsNotNull();
            await Assert.That(HdsThemeCatalog.Find("NoSuchPalette")).IsNull();
        }, CancellationToken.None);
    }

    /// <summary>
    ///     <c>DisplayName</c> is derived from the id rather than declared beside
    ///     it: <c>CatppuccinMocha</c> reads as "Catppuccin Mocha", single-word
    ///     names are left alone. A label is presentation, not a design token.
    /// </summary>
    [Test]
    public async Task DisplayName_Is_Derived_From_The_Palette_Id()
    {
        await Assert.That(HdsThemeCatalog.Find("CatppuccinMocha")!.DisplayName).IsEqualTo("Catppuccin Mocha");
        await Assert.That(HdsThemeCatalog.Find("Vapor")!.DisplayName).IsEqualTo("Vapor");
        await Assert.That(HdsThemeCatalog.Find("Lumen")!.DisplayName).IsEqualTo("Lumen");
        await Assert.That(HdsThemeCatalog.Find("NoSuchPalette")).IsNull();
    }

    private static Color ColorOf(IBrush brush) =>
        brush as SolidColorBrush is { } solid
            ? solid.Color
            : throw new InvalidOperationException(
                $"'{brush.GetType().Name}' is not a SolidColorBrush — the thumbnail cannot read a colour from it.");

    /// <summary>
    ///     The colour a brush key resolves to inside its own file: either the
    ///     literal it carries or the <c>{StaticResource …}</c> colour it points
    ///     at. This is the file-side of the assertion — the runtime side is
    ///     <see cref="HdsThemeCatalog" />.
    /// </summary>
    private static Color DeclaredColor(string path, string brushKey)
    {
        var doc = XDocument.Load(path);
        XElement? brush = Resource(doc, brushKey);
        string? declared = (string?)brush?.Attribute("Color");

        if (declared is not null
            && declared.StartsWith("{StaticResource ", StringComparison.Ordinal)
            && declared.EndsWith('}'))
        {
            string referenced = declared["{StaticResource ".Length..^1].Trim();
            declared = (string?)Resource(doc, referenced)?.Value;
        }

        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} declares no colour for '{brushKey}'.");
        }

        return Color.Parse(declared);
    }

    /// <summary>Raw text of a keyed resource in a .axaml file.</summary>
    private static string? ReadValue(string path, string key) =>
        (string?)Resource(XDocument.Load(path), key)?.Value;

    private static XElement? Resource(XDocument doc, string key) =>
        doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(XName.Get("Key", XNamespace)) == key);

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
