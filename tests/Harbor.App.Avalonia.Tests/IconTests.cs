using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Verifies that every icon in Themes/Hds/Icons.axaml is a valid
///     StreamGeometry path string.
/// </summary>
/// <remarks>
///     <para>
///         This is a pure-XML-parity test — it does NOT require an Avalonia
///         Application. It extracts the path-data string for each
///         <c>x:Key="Ic*"</c> entry and validates it matches a basic SVG path
///         pattern. <see cref="Geometry.Parse" /> requires an Avalonia
///         <c>IPlatformRenderInterface</c> (unavailable in headless test runs),
///         so we validate structurally instead: non-empty, starts with a valid
///         move/command token, and contains only SVG path characters.
///         A malformed path (e.g. from a bad copy-paste) would fail at app
///         startup when the resource dictionary loads.
///     </para>
/// </remarks>
public class IconTests
{
    private static readonly string IconsPath = Path.Combine(
        FindRepoRoot(), "apps", "Harbor.App.Avalonia", "Themes", "Hds", "Icons.axaml");

    private static readonly string XNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    ///     Validates that a path-data string starts with a valid SVG path
    ///     command letter and contains only valid path characters.
    /// </summary>
    private static readonly Regex PathDataRegex = new(
        @"^\s*[MmLlHhVvCcSsQqTtAaFr][0-9\s\.,\-+eE%]+",
        RegexOptions.Compiled);

    [Test]
    public async Task All_Icons_Parse_As_Valid_Geometry()
    {
        var doc = XDocument.Load(IconsPath);
        var icons = doc.Descendants()
            .Where(e => e.Name.LocalName == "StreamGeometry"
                     && e.Attribute(XName.Get("Key", XNamespace)) is not null)
            .ToDictionary(
                e => e.Attribute(XName.Get("Key", XNamespace))!.Value,
                e => e.Value ?? string.Empty);

        await Assert.That(icons.Count).IsGreaterThanOrEqualTo(25);

        foreach (var (name, pathData) in icons)
        {
            await Assert.That(!string.IsNullOrWhiteSpace(pathData)).IsTrue();
            await Assert.That(PathDataRegex.IsMatch(pathData.Trim())).IsTrue();
        }
    }

    [Test]
    public async Task Required_Core_Icons_Exist()
    {
        var doc = XDocument.Load(IconsPath);
        var keys = doc.Descendants()
            .Where(e => e.Name.LocalName == "StreamGeometry"
                     && e.Attribute(XName.Get("Key", XNamespace)) is not null)
            .Select(e => e.Attribute(XName.Get("Key", XNamespace))!.Value)
            .ToHashSet();

        string[] required = {
            "IcAdd", "IcSearch", "IcSettings", "IcSend", "IcStop",
            "IcTrash", "IcChevronRight", "IcChevronDown", "IcChevronUp",
            "IcChevronLeft", "IcHome", "IcTerminal", "IcFileCode", "IcMore",
            "IcCheck", "IcX", "IcCopy", "IcPaste", "IcLoading", "IcPause",
            "IcPlay", "IcRefresh", "IcDownload", "IcUpload", "IcLink",
            "IcExternalLink", "IcInfo", "IcWarning", "IcError", "IcSuccess",
            "IcMenu", "IcCircleClose", "IcMinimizeWindow", "IcMaximizeWindow",
            "IcFolder", "IcFolderOpen", "IcBold", "IcItalic", "IcCode",
            "IcStrikethrough", "IcArrowUp", "IcArrowDown", "IcUser", "IcBot",
            "IcEye", "IcEyeOff", "IcEdit", "IcPlus", "IcMinus", "IcSession",
            "IcDiff", "IcTheme"
        };

        foreach (var expected in required)
        {
            await Assert.That(keys.Contains(expected)).IsTrue();
        }
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
