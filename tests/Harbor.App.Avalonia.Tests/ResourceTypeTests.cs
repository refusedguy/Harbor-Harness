using System.Xml;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Static audits of resource types and style-selector conventions across
///     all <c>.axaml</c> files in the Avalonia app. No running Application
///     is required — these are pure-XML-parity tests.
/// </summary>
public class ResourceTypeTests
{
    private const string XNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Test]
    public async Task Duration_Resources_Are_TimeSpan()
    {
        var axamlFiles = Directory.GetFiles(
            Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia"),
            "*.axaml",
            SearchOption.AllDirectories);

        var violations = new List<string>();
        foreach (var file in axamlFiles)
        {
            var doc = XDocument.Load(file);
            foreach (var elem in doc.Descendants()
                .Where(e => e.Attribute(XName.Get("Key", XNamespace)) is not null))
            {
                var key = elem.Attribute(XName.Get("Key", XNamespace))?.Value;
                if (key is null) continue;

                if (key.Contains("Motion") || key.Contains("Ease") || key.Contains("Transition"))
                {
                    var sysNs = "clr-namespace:System;assembly=mscorlib";
                    if (elem.Name.NamespaceName != sysNs)
                    {
                        violations.Add($"{file}: {key} is {elem.Name.LocalName} (expected sys:TimeSpan)");
                    }
                }
            }
        }

        foreach (var v in violations)
            Console.WriteLine(v);
        await Assert.That(violations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CornerRadius_Resources_Are_CornerRadius()
    {
        var axamlFiles = Directory.GetFiles(
            Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia"),
            "*.axaml",
            SearchOption.AllDirectories);

        var violations = new List<string>();
        foreach (var file in axamlFiles)
        {
            var doc = XDocument.Load(file);
            foreach (var elem in doc.Descendants()
                .Where(e => e.Attribute(XName.Get("Key", XNamespace)) is not null))
            {
                var key = elem.Attribute(XName.Get("Key", XNamespace))?.Value;
                if (key is null) continue;

                if (key.Contains("Radius") && !key.Contains("RadiusFull") && !key.Contains("RadiusNone"))
                {
                    if (elem.Name.LocalName != "CornerRadius")
                    {
                        violations.Add($"{file}: {key} is {elem.Name.LocalName} (expected CornerRadius)");
                    }
                }
            }
        }

        foreach (var v in violations)
            Console.WriteLine(v);
        await Assert.That(violations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PointerOver_Selectors_Use_Concrete_Type_Prefix()
    {
        var axamlFiles = Directory.GetFiles(
            Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia"),
            "*.axaml",
            SearchOption.AllDirectories);

        var violations = new List<string>();
        foreach (var file in axamlFiles)
        {
            var doc = XDocument.Load(file);
            foreach (var style in doc.Descendants()
                .Where(e => e.Name.LocalName == "Style"
                         && e.Attribute(XName.Get("Selector", XNamespace)) is not null))
            {
                var selector = style.Attribute(XName.Get("Selector", XNamespace))?.Value ?? string.Empty;
                if (selector.Contains(":pointerover") && selector.StartsWith('.'))
                {
                    violations.Add($"{file}: {selector}");
                }
            }
        }

        foreach (var v in violations)
            Console.WriteLine(v);
        await Assert.That(violations.Count).IsEqualTo(0);
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
