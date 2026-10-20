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
/// <remarks>
///     #1859: every guard here reports ALL of its violators in one run. The
///     detection is unchanged; what changed is the reporting step: the
///     violators used to be printed to stdout and then summarised by a single
///     <c>Assert.That(violations.Count).IsEqualTo(0)</c>, which named none of
///     them. Now each violator is its own assertion inside an
///     <c>Assert.Multiple()</c> scope, so one CI run names every offender.
/// </remarks>
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
        var candidates = 0;
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
                    candidates++;
                    var sysNs = "clr-namespace:System;assembly=mscorlib";
                    if (elem.Name.NamespaceName != sysNs)
                    {
                        violations.Add($"{file}: {key} is {elem.Name.LocalName} (expected sys:TimeSpan)");
                    }
                }
            }
        }

        await Assert.That(candidates).IsGreaterThan(0)
            .Because("the rule has to be applied to something: with no Motion/Ease/Transition resource in the app it proves nothing");

        await GuardViolations.ReportAsync(violations);
    }

    [Test]
    public async Task CornerRadius_Resources_Are_CornerRadius()
    {
        var axamlFiles = Directory.GetFiles(
            Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia"),
            "*.axaml",
            SearchOption.AllDirectories);

        var violations = new List<string>();
        var candidates = 0;
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
                    candidates++;
                    if (elem.Name.LocalName != "CornerRadius")
                    {
                        violations.Add($"{file}: {key} is {elem.Name.LocalName} (expected CornerRadius)");
                    }
                }
            }
        }

        await Assert.That(candidates).IsGreaterThan(0)
            .Because("the rule has to be applied to something: with no Radius resource in the app it proves nothing");

        await GuardViolations.ReportAsync(violations);
    }

    [Test]
    public async Task PointerOver_Selectors_Use_Concrete_Type_Prefix()
    {
        var axamlFiles = Directory.GetFiles(
            Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia"),
            "*.axaml",
            SearchOption.AllDirectories);

        var violations = new List<string>();
        var candidates = 0;
        foreach (var file in axamlFiles)
        {
            var doc = XDocument.Load(file);
            // The Selector attribute is NOT in the xaml namespace: Avalonia writes
            // it plain (`<Style Selector=":pointerover">`), so looking it up under
            // XNamespace matched nothing at all and this guard examined zero
            // elements. Found by the non-vacuity assertion #1859 added below.
            foreach (var style in doc.Descendants()
                .Where(e => e.Name.LocalName == "Style"
                         && e.Attribute(XName.Get("Selector")) is not null))
            {
                var selector = style.Attribute(XName.Get("Selector"))?.Value ?? string.Empty;
                if (selector.Contains(":pointerover"))
                {
                    candidates++;
                    if (selector.StartsWith('.'))
                    {
                        violations.Add($"{file}: {selector}");
                    }
                }
            }
        }

        await Assert.That(candidates).IsGreaterThan(0)
            .Because("the rule has to be applied to something: with no :pointerover selector in the app it proves nothing");

        await GuardViolations.ReportAsync(violations);
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
